using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Selects the platform's trusted CA bundle for the pinned FFmpeg/OpenSSL build.
/// Static OpenSSL builds don't necessarily use macOS Keychain or the Windows certificate
/// stores automatically.</summary>
internal static class FfmpegCertificateTrust
{
    private static readonly object WindowsBundleGate = new();

    public static void Configure(ProcessStartInfo startInfo, string toolsDirectory, ILogger logger)
    {
        var bundle = FindSystemBundle() ?? (OperatingSystem.IsWindows()
            ? TryCreateWindowsBundle(toolsDirectory, logger)
            : null);
        if (bundle is null)
        {
            logger.LogWarning("No system CA bundle was found for FFmpeg; HTTPS media transfers may fail certificate verification.");
            return;
        }

        startInfo.Environment["SSL_CERT_FILE"] = bundle;
        logger.LogDebug("Using system CA bundle {CertificateBundle} for FFmpeg.", bundle);
    }

    private static string? FindSystemBundle()
    {
        string[] candidates = OperatingSystem.IsMacOS()
            ? ["/etc/ssl/cert.pem", "/System/Library/OpenSSL/cert.pem"]
            : OperatingSystem.IsLinux()
                ? ["/etc/ssl/certs/ca-certificates.crt", "/etc/pki/tls/certs/ca-bundle.crt", "/etc/ssl/cert.pem"]
                : [];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? TryCreateWindowsBundle(string toolsDirectory, ILogger logger)
    {
        var bundlePath = Path.Combine(Path.GetFullPath(toolsDirectory), "certificates", "windows-ca-bundle.pem");
        lock (WindowsBundleGate)
        {
            try
            {
                if (File.Exists(bundlePath) && new FileInfo(bundlePath).Length > 0)
                    return bundlePath;

                var contents = new StringBuilder();
                var thumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                {
                    try
                    {
                        using var store = new X509Store(StoreName.Root, location);
                        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                        foreach (X509Certificate2 certificate in store.Certificates)
                        {
                            if (string.IsNullOrWhiteSpace(certificate.Thumbprint) || !thumbprints.Add(certificate.Thumbprint))
                                continue;
                            contents.AppendLine(certificate.ExportCertificatePem());
                        }
                    }
                    catch (CryptographicException exception)
                    {
                        logger.LogDebug(exception, "Could not read the {StoreLocation} Windows root certificate store.", location);
                    }
                }

                if (contents.Length == 0)
                    return null;

                Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
                var temporaryPath = bundlePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporaryPath, contents.ToString(), new UTF8Encoding(false));
                    File.Move(temporaryPath, bundlePath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                    catch (IOException) { }
                }
                return bundlePath;
            }
            catch (Exception exception) when (
                exception is CryptographicException or IOException or UnauthorizedAccessException or SecurityException)
            {
                logger.LogWarning(exception, "Could not create an FFmpeg CA bundle from the Windows root certificate stores.");
                return null;
            }
        }
    }
}
