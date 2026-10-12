using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace MediaPager.App.Api.Services;

public sealed class ApiRestartCoordinator(
    string[] startupArguments,
    string communityDirectory,
    bool externallySupervised,
    bool desktopHosted,
    IHostApplicationLifetime lifetime,
    ILogger<ApiRestartCoordinator> logger)
{
    public void RestartAfterResponse(HttpResponse response)
    {
        response.OnCompleted(() =>
        {
            if (!externallySupervised && !desktopHosted)
            {
                try
                {
                    StartBootstrapper();
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not start the API restart bootstrapper.");
                    return Task.CompletedTask;
                }
            }

            lifetime.StopApplication();
            return Task.CompletedTask;
        });
    }

    private void StartBootstrapper()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The API executable could not be located.");
        var apiAssembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(apiAssembly) || !File.Exists(apiAssembly))
            throw new InvalidOperationException("The API assembly could not be located.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(apiAssembly);
        startInfo.ArgumentList.Add("--bootstrap-api-restart");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(Path.GetFullPath(communityDirectory));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(startupArguments))));

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("The API restart bootstrapper did not start.");
    }
}
