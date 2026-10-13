using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Services;

public sealed class GitSshPrivateKeyStore
{
    public const string SettingKey = "Git:SshPrivateKey";
    private readonly AuthDbContext _database;
    private readonly IDataProtector _protector;

    public GitSshPrivateKeyStore(AuthDbContext database, IDataProtectionProvider protection)
    {
        _database = database;
        _protector = protection.CreateProtector("MediaPager.Git.PrivateKey.v1");
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
        await _database.RuntimeSettings.AsNoTracking().AnyAsync(setting => setting.Key == SettingKey, cancellationToken);

    public async Task<string?> GetPrivateKeyAsync(CancellationToken cancellationToken = default)
    {
        var value = await _database.RuntimeSettings.AsNoTracking()
            .Where(setting => setting.Key == SettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return _protector.Unprotect(value);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new InvalidOperationException("The stored Git SSH key cannot be decrypted. Enter the key again in Settings.", exception);
        }
    }

    public async Task SetPrivateKeyAsync(string privateKey, CancellationToken cancellationToken = default)
    {
        var normalized = privateKey.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length is < 100 or > 64_000 ||
            !normalized.StartsWith("-----BEGIN ", StringComparison.Ordinal) ||
            !normalized.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            throw new ArgumentException("Paste a valid OpenSSH or PEM private key.", nameof(privateKey));

        var protectedKey = _protector.Protect(normalized);
        var setting = await _database.RuntimeSettings.FindAsync([SettingKey], cancellationToken);
        if (setting is null)
            _database.RuntimeSettings.Add(new RuntimeSetting { Key = SettingKey, Value = protectedKey });
        else
            setting.Value = protectedKey;
        await _database.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var setting = await _database.RuntimeSettings.FindAsync([SettingKey], cancellationToken);
        if (setting is null) return;
        _database.RuntimeSettings.Remove(setting);
        await _database.SaveChangesAsync(cancellationToken);
    }
}

public sealed class GitSshPrivateKeyProvider(
    IServiceScopeFactory scopeFactory,
    ILogger<GitSshPrivateKeyProvider> logger) : IGitPrivateKeyProvider
{
    public async Task<IGitPrivateKeyLease?> CreateLeaseAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GitSshPrivateKeyStore>();
        var privateKey = await store.GetPrivateKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            var legacyPath = Environment.GetEnvironmentVariable(GitRepoUrl.SshKeyPathVariable);
            if (!string.IsNullOrWhiteSpace(legacyPath) && !File.Exists(legacyPath))
                throw new InvalidOperationException($"{GitRepoUrl.SshKeyPathVariable} points at '{legacyPath}', which does not exist.");
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"mediapager-git-key-{Guid.NewGuid():N}");
        var bytes = Encoding.UTF8.GetBytes(privateKey);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await stream.WriteAsync(bytes, cancellationToken);
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        logger.LogDebug("Created a temporary SSH identity for a Git operation.");
        return new TemporaryKeyLease(path);
    }

    private sealed class TemporaryKeyLease(string path) : IGitPrivateKeyLease
    {
        public string Path { get; } = path;

        public ValueTask DisposeAsync()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var length = new FileInfo(Path).Length;
                    using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        var zeros = new byte[Math.Min(length, 16 * 1024)];
                        long remaining = length;
                        while (remaining > 0)
                        {
                            var count = (int)Math.Min(zeros.Length, remaining);
                            stream.Write(zeros, 0, count);
                            remaining -= count;
                        }
                    }
                    File.Delete(Path);
                }
            }
            catch { /* Best-effort cleanup; never mask the Git operation result. */ }
            return ValueTask.CompletedTask;
        }
    }
}
