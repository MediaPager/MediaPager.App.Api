using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MediaPager.App.Api.Services.Workers;

internal sealed record WindowsAppContainerLaunch(
    Process Process,
    StreamWriter StandardInput,
    StreamReader StandardOutput,
    StreamReader StandardError,
    string ProfileName);

/// <summary>Creates a worker with a fresh, network-isolated AppContainer identity. Its only
/// filesystem grant is a temporary private directory containing the worker and plugin files.</summary>
internal static class WindowsAppContainerWorker
{
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorAlreadyExists = 183;

    public static WindowsAppContainerLaunch Start(
        string apiAssembly,
        string pluginAssembly,
        string pluginDirectory,
        string workerHome,
        string pluginId)
    {
        var profileName = $"MediaPager.Worker.{Guid.NewGuid():N}";
        IntPtr appContainerSid = IntPtr.Zero;
        try
        {
            appContainerSid = CreateProfile(profileName);
            var sidText = ConvertSidToText(appContainerSid);
            var staged = StageWorkerFiles(apiAssembly, pluginAssembly, pluginDirectory, workerHome);
            GrantHomeAccess(workerHome, sidText);

            var executablePath = GetExecutablePath(staged);
            var arguments = GetArguments(
                staged.ApiAssembly,
                staged.PluginAssembly,
                pluginId,
                workerHome,
                string.Equals(executablePath, staged.ApiAppHost, StringComparison.OrdinalIgnoreCase));
            var commandLine = BuildCommandLine(executablePath, arguments);
            return StartProcess(executablePath, commandLine, workerHome, appContainerSid, profileName);
        }
        catch
        {
            _ = DeleteAppContainerProfile(profileName);
            _ = TryDeleteWorkerHome(workerHome);
            throw;
        }
        finally
        {
            if (appContainerSid != IntPtr.Zero) _ = FreeSid(appContainerSid);
        }
    }

    public static void DeleteProfile(string profileName) => _ = DeleteAppContainerProfile(profileName);

    public static bool TryDeleteWorkerHome(string workerHome)
    {
        // AppContainer shutdown can release mapped runtime DLLs shortly after the process
        // handle signals exit, especially under Windows-on-ARM x64 emulation.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (Directory.Exists(workerHome)) Directory.Delete(workerHome, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 49) return false;
                Thread.Sleep(200);
            }
        }
        return false;
    }

    private static IntPtr CreateProfile(string profileName)
    {
        var result = CreateAppContainerProfile(
            profileName,
            $"MediaPager plugin worker {profileName[^8..]}",
            "Temporary network-isolated MediaPager plugin worker",
            IntPtr.Zero,
            0,
            out var sid);
        if (result == 0) return sid;
        if (result != (unchecked((int)0x80070000) | ErrorAlreadyExists))
            throw new InvalidOperationException($"CreateAppContainerProfile failed with HRESULT 0x{result:X8}.");

        result = DeriveAppContainerSidFromAppContainerName(profileName, out sid);
        if (result != 0)
            throw new InvalidOperationException($"DeriveAppContainerSidFromAppContainerName failed with HRESULT 0x{result:X8}.");
        return sid;
    }

    private static string ConvertSidToText(IntPtr sid)
    {
        if (!ConvertSidToStringSid(sid, out var textPointer))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not convert the AppContainer SID to text.");
        try
        {
            return Marshal.PtrToStringUni(textPointer)
                ?? throw new InvalidOperationException("Windows returned an empty AppContainer SID.");
        }
        finally
        {
            _ = LocalFree(textPointer);
        }
    }

    private static (string ApiAssembly, string PluginAssembly, string ApiAppHost, string DotnetDirectory, bool ApiSelfContained) StageWorkerFiles(
        string apiAssembly,
        string pluginAssembly,
        string pluginDirectory,
        string workerHome)
    {
        var apiSourceDirectory = Path.GetDirectoryName(apiAssembly)
            ?? throw new InvalidOperationException("The API assembly has no directory.");
        var apiSelfContained = IsSelfContainedApi(apiAssembly);
        var apiDirectory = Path.Combine(workerHome, "app");
        var stagedPluginDirectory = Path.Combine(workerHome, "plugin", Path.GetFileName(pluginDirectory));
        Directory.CreateDirectory(apiDirectory);
        Directory.CreateDirectory(stagedPluginDirectory);

        foreach (var file in Directory.EnumerateFiles(apiSourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "MediaPager.App.Api.exe", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(apiDirectory, name), overwrite: true);
        }

        var runtimeAssets = Path.Combine(apiSourceDirectory, "runtimes");
        if (Directory.Exists(runtimeAssets))
            CopyDirectory(runtimeAssets, Path.Combine(apiDirectory, "runtimes"));
        CopyDirectory(pluginDirectory, stagedPluginDirectory);

        var stagedApiAssembly = Path.Combine(apiDirectory, Path.GetFileName(apiAssembly));
        var stagedPluginAssembly = Path.Combine(stagedPluginDirectory, Path.GetFileName(pluginAssembly));
        if (!File.Exists(stagedApiAssembly) || !File.Exists(stagedPluginAssembly))
            throw new FileNotFoundException("The AppContainer worker staging directory is incomplete.");
        var stagedApiAppHost = Path.Combine(apiDirectory, "MediaPager.App.Api.exe");
        var dotnetDirectory = apiSelfContained ? string.Empty : StageDotnetRuntime(workerHome);
        return (stagedApiAssembly, stagedPluginAssembly,
            File.Exists(stagedApiAppHost) ? stagedApiAppHost : string.Empty,
            dotnetDirectory,
            apiSelfContained);
    }

    private static bool IsSelfContainedApi(string apiAssembly)
    {
        var runtimeConfig = Path.ChangeExtension(apiAssembly, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfig)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
        return document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions) &&
               runtimeOptions.TryGetProperty("includedFrameworks", out _);
    }

    private static string StageDotnetRuntime(string workerHome)
    {
        var runtimeDirectory = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory());
        var runtimeVersion = Path.GetFileName(runtimeDirectory);
        var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDirectory, "../../../"));
        var stagedRoot = Path.Combine(workerHome, "dotnet");
        var hostFxrSource = Path.Combine(dotnetRoot, "host", "fxr", runtimeVersion);
        var coreRuntimeSource = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App", runtimeVersion);
        var aspNetRuntimeSource = Path.Combine(dotnetRoot, "shared", "Microsoft.AspNetCore.App", runtimeVersion);
        foreach (var required in new[] { hostFxrSource, coreRuntimeSource, aspNetRuntimeSource })
            if (!Directory.Exists(required))
                throw new DirectoryNotFoundException($"The installed .NET ARM64 runtime is missing '{required}'.");

        Directory.CreateDirectory(stagedRoot);
        File.Copy(Path.Combine(dotnetRoot, "dotnet.exe"), Path.Combine(stagedRoot, "dotnet.exe"), overwrite: true);
        CopyDirectory(hostFxrSource, Path.Combine(stagedRoot, "host", "fxr", runtimeVersion));
        CopyDirectory(coreRuntimeSource, Path.Combine(stagedRoot, "shared", "Microsoft.NETCore.App", runtimeVersion));
        CopyDirectory(aspNetRuntimeSource, Path.Combine(stagedRoot, "shared", "Microsoft.AspNetCore.App", runtimeVersion));
        return stagedRoot;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string GetExecutablePath(
        (string ApiAssembly, string PluginAssembly, string ApiAppHost, string DotnetDirectory, bool ApiSelfContained) staged)
    {
        if (staged.ApiSelfContained && staged.ApiAppHost.Length > 0) return staged.ApiAppHost;

        var dotnetHost = Path.Combine(staged.DotnetDirectory, "dotnet.exe");
        if (File.Exists(dotnetHost)) return dotnetHost;
        throw new FileNotFoundException("Could not stage the .NET host needed to start a Windows plugin worker.", dotnetHost);
    }

    private static IReadOnlyList<string> GetArguments(
        string stagedApiAssembly,
        string stagedPluginAssembly,
        string pluginId,
        string workerHome,
        bool useApiAppHost)
    {
        var arguments = new List<string>();
        if (!useApiAppHost)
            arguments.Add(stagedApiAssembly);
        arguments.Add("--plugin-worker");
        arguments.Add(stagedPluginAssembly);
        arguments.Add(pluginId);
        arguments.Add(workerHome);
        return arguments;
    }

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { QuoteArgument(executablePath) }.Concat(arguments.Select(QuoteArgument)));

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            return argument;

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static void GrantHomeAccess(string workerHome, string sid)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var icacls = Path.Combine(systemRoot, "System32", "icacls.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = icacls,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(workerHome);
        startInfo.ArgumentList.Add("/grant");
        startInfo.ArgumentList.Add($"*{sid}:(OI)(CI)M");
        startInfo.ArgumentList.Add("/T");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("/Q");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start icacls to grant the AppContainer its private worker directory.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"icacls could not grant the AppContainer access to its private worker directory (exit code {process.ExitCode}): " +
                outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult());
    }

    private static WindowsAppContainerLaunch StartProcess(
        string executablePath,
        string commandLine,
        string workerHome,
        IntPtr appContainerSid,
        string profileName)
    {
        SafeFileHandle? stdinRead = null;
        SafeFileHandle? stdinWrite = null;
        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stdoutWrite = null;
        SafeFileHandle? stderrRead = null;
        SafeFileHandle? stderrWrite = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr handleList = IntPtr.Zero;
        IntPtr securityCapabilitiesPointer = IntPtr.Zero;
        ProcessInformation processInformation = default;
        var processCreated = false;

        try
        {
            var pipeSecurity = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true,
            };
            if (!CreatePipe(out stdinRead, out stdinWrite, ref pipeSecurity, 0) ||
                !CreatePipe(out stdoutRead, out stdoutWrite, ref pipeSecurity, 0) ||
                !CreatePipe(out stderrRead, out stderrWrite, ref pipeSecurity, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Windows worker RPC pipes.");
            SetParentHandleNonInheritable(stdinWrite);
            SetParentHandleNonInheritable(stdoutRead);
            SetParentHandleNonInheritable(stderrRead);

            var handleSize = checked((nuint)(IntPtr.Size * 3));
            if (!InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref handleSize) &&
                Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the Windows worker process attribute list.");
            attributeList = Marshal.AllocHGlobal(checked((int)handleSize));
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref handleSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize the Windows worker process attribute list.");

            handleList = Marshal.AllocHGlobal(checked((int)(IntPtr.Size * 3)));
            Marshal.WriteIntPtr(handleList, 0, stdinRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, stderrWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(
                    attributeList, 0, ProcThreadAttributeHandleList, handleList,
                    (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict inherited worker handles.");

            securityCapabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities
            {
                AppContainerSid = appContainerSid,
                Capabilities = IntPtr.Zero,
                CapabilityCount = 0,
                Reserved = 0,
            }, securityCapabilitiesPointer, fDeleteOld: false);
            if (!UpdateProcThreadAttribute(
                    attributeList, 0, ProcThreadAttributeSecurityCapabilities, securityCapabilitiesPointer,
                    (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set the Windows AppContainer process identity.");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = stdinRead.DangerousGetHandle(),
                    StandardOutput = stdoutWrite.DangerousGetHandle(),
                    StandardError = stderrWrite.DangerousGetHandle(),
                },
                AttributeList = attributeList,
            };
            var startupPointer = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoEx>());
            var commandLineBuffer = new StringBuilder(commandLine);
            try
            {
                Marshal.StructureToPtr(startup, startupPointer, fDeleteOld: false);
                if (!CreateProcess(
                        executablePath,
                        commandLineBuffer,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        inheritHandles: true,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow,
                        IntPtr.Zero,
                        workerHome,
                        startupPointer,
                        out processInformation))
                {
                    var error = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException(
                        $"Could not start the Windows AppContainer plugin worker (Win32 error {error}).",
                        new Win32Exception(error));
                }
                processCreated = true;
            }
            finally
            {
                Marshal.FreeHGlobal(startupPointer);
            }

            stdinRead.Dispose(); stdinRead = null;
            stdoutWrite.Dispose(); stdoutWrite = null;
            stderrWrite.Dispose(); stderrWrite = null;

            var process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            var inputWriter = new StreamWriter(
                new FileStream(stdinWrite, FileAccess.Write, 4096, isAsync: false), new UTF8Encoding(false))
            { AutoFlush = true };
            stdinWrite = null;
            var outputReader = new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false), new UTF8Encoding(false));
            stdoutRead = null;
            var errorReader = new StreamReader(new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false), new UTF8Encoding(false));
            stderrRead = null;
            return new WindowsAppContainerLaunch(process, inputWriter, outputReader, errorReader, profileName);
        }
        catch
        {
            if (processCreated)
            {
                _ = TerminateProcess(processInformation.ProcessHandle, 1);
                _ = WaitForSingleObject(processInformation.ProcessHandle, 5000);
            }
            throw;
        }
        finally
        {
            stdinRead?.Dispose(); stdinWrite?.Dispose();
            stdoutRead?.Dispose(); stdoutWrite?.Dispose();
            stderrRead?.Dispose(); stderrWrite?.Dispose();
            if (securityCapabilitiesPointer != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilitiesPointer);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (processInformation.ThreadHandle != IntPtr.Zero) _ = CloseHandle(processInformation.ThreadHandle);
            if (processInformation.ProcessHandle != IntPtr.Zero) _ = CloseHandle(processInformation.ProcessHandle);
        }
    }

    private static void SetParentHandleNonInheritable(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect a parent worker pipe handle from inheritance.");
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CreateAppContainerProfile(
        string appContainerName,
        string displayName,
        string description,
        IntPtr capabilities,
        uint capabilityCount,
        out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount,
        uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, uint attribute,
        IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        IntPtr startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }
}
