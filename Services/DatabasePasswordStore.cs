using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Services;

/// <summary>Database connection passwords are stored separately from ordinary settings,
/// encrypted at rest, and exposed to the UI only as a configured/not-configured flag.</summary>
public sealed class DatabasePasswordStore(AuthDbContext database, IDataProtectionProvider protection, IConfiguration configuration)
{
    public const string SettingKey = "Settings:Db:Password";
    private const string ProtectedPrefix = "enc:v1:";
    private readonly IDataProtector _protector = protection.CreateProtector("MediaPager.Settings.Db.Password.v1");

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace(await GetPlaintextAsync(cancellationToken));

    public async Task<string?> GetProtectedValueAsync(CancellationToken cancellationToken = default) =>
        await database.RuntimeSettings.AsNoTracking().Where(setting => setting.Key == SettingKey)
            .Select(setting => setting.Value).FirstOrDefaultAsync(cancellationToken);

    public async Task<string?> GetPlaintextAsync(CancellationToken cancellationToken = default)
    {
        var stored = await GetProtectedValueAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(stored))
            return stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
                ? _protector.Unprotect(stored[ProtectedPrefix.Length..])
                : stored; // Legacy config rows are reprotected by SeedOrMigrateAsync.
        return SettingsConfigurationKeys.ReadConfiguredValue(configuration, SettingKey);
    }

    public async Task SeedOrMigrateAsync(CancellationToken cancellationToken = default)
    {
        var existing = await database.RuntimeSettings.FindAsync([SettingKey], cancellationToken);
        if (existing is not null)
        {
            if (!existing.Value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                existing.Value = ProtectedPrefix + _protector.Protect(existing.Value);
                await database.SaveChangesAsync(cancellationToken);
            }
            return;
        }
        var configured = SettingsConfigurationKeys.ReadConfiguredValue(configuration, SettingKey);
        if (string.IsNullOrWhiteSpace(configured)) return;
        database.RuntimeSettings.Add(new RuntimeSetting { Key = SettingKey, Value = ProtectedPrefix + _protector.Protect(configured) });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task SetAsync(string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("A database password is required.", nameof(password));
        var setting = await database.RuntimeSettings.FindAsync([SettingKey], cancellationToken);
        var protectedValue = ProtectedPrefix + _protector.Protect(password);
        if (setting is null) database.RuntimeSettings.Add(new RuntimeSetting { Key = SettingKey, Value = protectedValue });
        else setting.Value = protectedValue;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var setting = await database.RuntimeSettings.FindAsync([SettingKey], cancellationToken);
        if (setting is null) return;
        database.RuntimeSettings.Remove(setting);
        await database.SaveChangesAsync(cancellationToken);
    }
}
