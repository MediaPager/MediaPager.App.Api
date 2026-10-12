using System.Runtime.InteropServices;

namespace MediaPager.App.Api.Services.Workers;

/// <summary>Applies Linux seccomp restrictions to every worker thread and, when supported,
/// a thread-synchronized Landlock filesystem allow-list before plugin code loads. Worker RPC
/// supplies network and process tools through the parent API process.</summary>
internal static class LinuxWorkerSyscallPolicy
{
    private const int PrSetNoNewPrivs = 38;
    private const uint SeccompSetModeFilter = 1;
    private const uint SeccompFilterFlagTsync = 1;
    private const uint SeccompReturnKillProcess = 0x80000000;
    private const uint SeccompReturnErrno = 0x00050000;
    private const uint SeccompReturnAllow = 0x7fff0000;
    private const int LandlockCreateRulesetSyscall = 444;
    private const int LandlockAddRuleSyscall = 445;
    private const int LandlockRestrictSelfSyscall = 446;
    private const long LandlockCreateRulesetVersion = 1;
    private const long LandlockRulePathBeneath = 1;
    private const long LandlockRestrictSelfTsync = 1;
    private const int OpenPath = 0x200000;
    private const int OpenCloseOnExec = 0x80000;
    private const ulong LandlockExecute = 1UL << 0;
    private const ulong LandlockWriteFile = 1UL << 1;
    private const ulong LandlockReadFile = 1UL << 2;
    private const ulong LandlockReadDirectory = 1UL << 3;
    private const ulong LandlockRemoveDirectory = 1UL << 4;
    private const ulong LandlockRemoveFile = 1UL << 5;
    private const ulong LandlockMakeDirectory = 1UL << 7;
    private const ulong LandlockMakeFile = 1UL << 8;
    private const ulong LandlockMakeSymlink = 1UL << 12;
    private const ulong LandlockRefer = 1UL << 13;
    private const ulong LandlockTruncate = 1UL << 14;
    private const ulong LandlockIoctlDevice = 1UL << 15;
    private const ushort BpfLoadWordAbsolute = 0x20;
    private const ushort BpfJumpEqual = 0x15;
    private const ushort BpfJumpSet = 0x45;
    private const ushort BpfReturn = 0x06;
    private const uint CloneThreadFlag = 0x00010000;

    public static void Apply(string pluginDirectory, string workerHome)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var (auditArchitecture, seccompSyscall, deniedSyscalls, cloneSyscall, clone3Syscall) = architecture switch
        {
            Architecture.X64 => (0xC000003EU, 317L,
                new[] { 41, 42, 43, 49, 50, 53, 59, 57, 58, 288, 322, 425 }, 56, 435),
            Architecture.Arm64 => (0xC00000B7U, 277L,
                new[] { 198, 199, 200, 201, 202, 203, 221, 117, 58, 242, 281, 425 }, 220, 435),
            _ => throw new PlatformNotSupportedException(
                $"Linux plugin worker syscall filtering is not implemented for {architecture}."),
        };

        if (Prctl(PrSetNoNewPrivs, 1, 0, 0, 0) != 0)
            throw new InvalidOperationException($"Could not set no_new_privs for plugin worker (errno {Marshal.GetLastWin32Error()}).");

        if (TryApplyFilesystemRestrictions(pluginDirectory, workerHome, out var filesystemError))
            Console.Error.WriteLine("[plugin-worker] Linux Landlock filesystem restrictions enabled.");
        else
            Console.Error.WriteLine($"[plugin-worker] Linux Landlock unavailable; filesystem access remains unrestricted: {filesystemError}");

        var filters = new List<SockFilter>
        {
            new(BpfLoadWordAbsolute, 0, 0, 4),
            new(BpfJumpEqual, 1, 0, auditArchitecture),
            new(BpfReturn, 0, 0, SeccompReturnKillProcess),
            new(BpfLoadWordAbsolute, 0, 0, 0),
        };
        foreach (var syscall in deniedSyscalls)
        {
            filters.Add(new SockFilter(BpfJumpEqual, 0, 1, checked((uint)syscall)));
            filters.Add(new SockFilter(BpfReturn, 0, 0, SeccompReturnErrno | (uint)1)); // EPERM
        }

        // Let the CLR create managed threads, but deny process-like clone calls.
        filters.Add(new SockFilter(BpfJumpEqual, 0, 3, checked((uint)cloneSyscall)));
        filters.Add(new SockFilter(BpfLoadWordAbsolute, 0, 0, 16)); // seccomp_data.args[0]
        filters.Add(new SockFilter(BpfJumpSet, 1, 0, CloneThreadFlag));
        filters.Add(new SockFilter(BpfReturn, 0, 0, SeccompReturnErrno | (uint)1)); // EPERM

        // Return ENOSYS for clone3 so libc can fall back to clone(2), where flags are inspectable.
        filters.Add(new SockFilter(BpfJumpEqual, 0, 1, checked((uint)clone3Syscall)));
        filters.Add(new SockFilter(BpfReturn, 0, 0, SeccompReturnErrno | (uint)38)); // ENOSYS
        filters.Add(new SockFilter(BpfReturn, 0, 0, SeccompReturnAllow));

        var filterArray = filters.ToArray();
        var pinned = GCHandle.Alloc(filterArray, GCHandleType.Pinned);
        var programPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SockFprog>());
        try
        {
            Marshal.StructureToPtr(new SockFprog
            {
                Length = checked((ushort)filterArray.Length),
                Filter = pinned.AddrOfPinnedObject(),
            }, programPointer, fDeleteOld: false);
            var result = Syscall(
                seccompSyscall,
                SeccompSetModeFilter,
                SeccompFilterFlagTsync,
                programPointer.ToInt64(),
                0,
                0);
            if (result != 0)
                throw new InvalidOperationException(
                    $"Could not install the plugin worker seccomp policy (result {result}, errno {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(programPointer);
            pinned.Free();
        }
    }

    private static bool TryApplyFilesystemRestrictions(
        string pluginDirectory,
        string workerHome,
        out string error)
    {
        var ruleset = -1;
        var directoryHandles = new List<int>();
        try
        {
            var abi = Syscall(LandlockCreateRulesetSyscall, 0, 0, LandlockCreateRulesetVersion, 0, 0);
            if (abi < 1)
                throw new InvalidOperationException($"Landlock is unavailable (result {abi}, errno {Marshal.GetLastWin32Error()}).");
            if (abi < 8)
                throw new InvalidOperationException(
                    $"Landlock ABI {abi} cannot synchronize filesystem restrictions to existing runtime threads (ABI 8 required).");

            var handledAccess = (1UL << 13) - 1;
            if (abi >= 2) handledAccess |= LandlockRefer;
            if (abi >= 3) handledAccess |= LandlockTruncate;
            if (abi >= 5) handledAccess |= LandlockIoctlDevice;
            var rulesetAttributes = new LandlockRulesetAttributes { HandledAccessFilesystem = handledAccess };
            var rulesetPointer = Marshal.AllocHGlobal(Marshal.SizeOf<LandlockRulesetAttributes>());
            try
            {
                Marshal.StructureToPtr(rulesetAttributes, rulesetPointer, fDeleteOld: false);
                ruleset = checked((int)Syscall(
                    LandlockCreateRulesetSyscall,
                    rulesetPointer.ToInt64(),
                    Marshal.SizeOf<LandlockRulesetAttributes>(),
                    0,
                    0,
                    0));
            }
            finally
            {
                Marshal.FreeHGlobal(rulesetPointer);
            }
            if (ruleset < 0)
                throw new InvalidOperationException($"Could not create a Landlock ruleset (errno {Marshal.GetLastWin32Error()}).");

            var readOnlyAccess = LandlockExecute | LandlockReadFile | LandlockReadDirectory;
            var writableAccess = readOnlyAccess | LandlockWriteFile | LandlockRemoveDirectory |
                                 LandlockRemoveFile | LandlockMakeDirectory | LandlockMakeFile |
                                 LandlockMakeSymlink;
            if (abi >= 2) writableAccess |= LandlockRefer;
            if (abi >= 3) writableAccess |= LandlockTruncate;

            var allowedDirectories = new Dictionary<string, ulong>(StringComparer.Ordinal);
            AddAllowedDirectory(pluginDirectory, readOnlyAccess, required: true);
            AddAllowedDirectory(RuntimeEnvironment.GetRuntimeDirectory(), readOnlyAccess, required: true);
            AddAllowedDirectory(workerHome, writableAccess, required: true);

            // Framework-dependent .NET installations load globalization data and native
            // framework support libraries from these system locations after startup.
            AddAllowedDirectory("/usr/lib", readOnlyAccess, required: false);
            AddAllowedDirectory("/usr/share", readOnlyAccess, required: false);

            foreach (var (path, access) in allowedDirectories)
            {
                var directoryHandle = Open(path, OpenPath | OpenCloseOnExec);
                if (directoryHandle < 0)
                    throw new InvalidOperationException($"Could not open Landlock allow-list directory '{path}' (errno {Marshal.GetLastWin32Error()}).");
                directoryHandles.Add(directoryHandle);

                var rule = new LandlockPathBeneathAttributes
                {
                    AllowedAccess = access,
                    ParentFileDescriptor = directoryHandle,
                };
                var rulePointer = Marshal.AllocHGlobal(Marshal.SizeOf<LandlockPathBeneathAttributes>());
                long result;
                try
                {
                    Marshal.StructureToPtr(rule, rulePointer, fDeleteOld: false);
                    result = Syscall(
                        LandlockAddRuleSyscall,
                        ruleset,
                        LandlockRulePathBeneath,
                        rulePointer.ToInt64(),
                        0,
                        0);
                }
                finally
                {
                    Marshal.FreeHGlobal(rulePointer);
                }
                if (result != 0)
                    throw new InvalidOperationException($"Could not add Landlock allow-list directory '{path}' (errno {Marshal.GetLastWin32Error()}).");
            }

            var allowedFiles = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(Path.GetFullPath, _ => LandlockReadFile, StringComparer.Ordinal);
            foreach (var (path, access) in allowedFiles)
            {
                var fileHandle = Open(path, OpenPath | OpenCloseOnExec);
                if (fileHandle < 0)
                    throw new InvalidOperationException($"Could not open Landlock allow-list file '{path}' (errno {Marshal.GetLastWin32Error()}).");
                directoryHandles.Add(fileHandle);

                var rule = new LandlockPathBeneathAttributes
                {
                    AllowedAccess = access,
                    ParentFileDescriptor = fileHandle,
                };
                var rulePointer = Marshal.AllocHGlobal(Marshal.SizeOf<LandlockPathBeneathAttributes>());
                long result;
                try
                {
                    Marshal.StructureToPtr(rule, rulePointer, fDeleteOld: false);
                    result = Syscall(
                        LandlockAddRuleSyscall,
                        ruleset,
                        LandlockRulePathBeneath,
                        rulePointer.ToInt64(),
                        0,
                        0);
                }
                finally
                {
                    Marshal.FreeHGlobal(rulePointer);
                }
                if (result != 0)
                    throw new InvalidOperationException($"Could not add Landlock allow-list file '{path}' (errno {Marshal.GetLastWin32Error()}).");
            }

            var restrictResult = Syscall(LandlockRestrictSelfSyscall, ruleset, LandlockRestrictSelfTsync, 0, 0, 0);
            if (restrictResult != 0)
                throw new InvalidOperationException($"Could not restrict the worker filesystem (errno {Marshal.GetLastWin32Error()}).");

            error = string.Empty;
            return true;

            void AddAllowedDirectory(string path, ulong access, bool required)
            {
                var fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath))
                {
                    if (required)
                        throw new DirectoryNotFoundException($"Landlock allow-list directory does not exist: {fullPath}");
                    return;
                }
                allowedDirectories[fullPath] = allowedDirectories.TryGetValue(fullPath, out var existing)
                    ? existing | access
                    : access;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            foreach (var handle in directoryHandles) _ = Close(handle);
            if (ruleset >= 0) _ = Close(ruleset);
        }
    }

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, long arg1, long arg2, long arg3, long arg4, long arg5);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

    [StructLayout(LayoutKind.Sequential)]
    private struct LandlockRulesetAttributes
    {
        public ulong HandledAccessFilesystem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LandlockPathBeneathAttributes
    {
        public ulong AllowedAccess;
        public int ParentFileDescriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFprog
    {
        public ushort Length;
        public IntPtr Filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SockFilter(ushort code, byte jumpTrue, byte jumpFalse, uint value)
    {
        public readonly ushort Code = code;
        public readonly byte JumpTrue = jumpTrue;
        public readonly byte JumpFalse = jumpFalse;
        public readonly uint Value = value;
    }
}
