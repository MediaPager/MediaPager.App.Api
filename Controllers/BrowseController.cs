using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("settings")]
[Authorize(Policy = "CanEditSettings")]
public sealed class BrowseController(ILoggerFactory loggerFactory) : ControllerBase
{
    // Open a native folder picker on the machine running the API and return the chosen path.
    // Used by settings so non-technical users can pick directories.
    [HttpGet("browse-folder")]
    public IActionResult BrowseFolder()
    {
        var logger = loggerFactory.CreateLogger("BrowseFolder");
        try
        {
            var path = PickFolder(logger);
            return string.IsNullOrWhiteSpace(path)
                ? NoContent()
                : Ok(new { path });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Folder picker failed");
            return Problem("Could not open the folder picker.", statusCode: 500);
        }
    }

    private static string? PickFolder(ILogger logger)
    {
        if (OperatingSystem.IsMacOS())
        {
            var script = "POSIX path of (choose folder with prompt \"Choose a folder\")";
            var (code, output) = Run("osascript", $"-e \"{script}\"");
            return code == 0 ? output.Trim() : null;
        }

        if (OperatingSystem.IsWindows())
        {
            // PowerShell FolderBrowserDialog.
            var ps = "Add-Type -AssemblyName System.Windows.Forms; " +
                "$d = New-Object System.Windows.Forms.FolderBrowserDialog; " +
                "if ($d.ShowDialog() -eq 'OK') { Write-Output $d.SelectedPath }";
            var (code, output) = Run("powershell", $"-NoProfile -Command \"{ps}\"");
            return code == 0 ? output.Trim() : null;
        }

        // Linux: try zenity, then kdialog.
        var (zcode, zout) = Run("zenity", "--file-selection --directory --title=\"Choose a folder\"");
        if (zcode == 0 && !string.IsNullOrWhiteSpace(zout)) return zout.Trim();
        var (kcode, kout) = Run("kdialog", "--getexistingdirectory ~");
        if (kcode == 0 && !string.IsNullOrWhiteSpace(kout)) return kout.Trim();
        logger.LogInformation("No supported folder picker (zenity/kdialog) found on Linux.");
        return null;
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return (-1, "");
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(60000);
            return (process.ExitCode, output);
        }
        catch (Exception exception)
        {
            return (-1, exception.Message);
        }
    }
}
