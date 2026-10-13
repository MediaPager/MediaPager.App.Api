using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MediaPager.App.Api.Services;

/// <summary>
/// Database connection details are configuration-only, but their provider/location must be
/// known before the database can be opened. This data-volume snapshot lives outside the auth
/// database and Settings UI; its password field is protected with Data Protection.
/// </summary>
public static class DatabaseBootstrapFile
{
    private static readonly string[] Keys =
    [
        RuntimeSettingKeys.DatabaseProvider,
        RuntimeSettingKeys.DatabasePath,
        RuntimeSettingKeys.DatabaseHost,
        RuntimeSettingKeys.DatabasePort,
        RuntimeSettingKeys.DatabaseCatalog,
        RuntimeSettingKeys.DatabaseUser,
        RuntimeSettingKeys.DatabaseSslMode,
        RuntimeSettingKeys.DatabaseConnectionTimeout,
    ];

    public static string PathFor(StorageLayout layout) =>
        Path.Combine(layout.ConfigDirectory, "database-bootstrap.json");

    public static void Write(string path, IConfiguration configuration, IDataProtectionProvider protection)
    {
        var database = new JsonObject();
        var provider = SettingsConfigurationKeys.ReadConfiguredValue(configuration, RuntimeSettingKeys.DatabaseProvider) ?? "Sqlite";
        foreach (var key in Keys)
        {
            if (key == RuntimeSettingKeys.DatabasePath &&
                !string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = SettingsConfigurationKeys.ReadConfiguredValue(configuration, key);
            if (!string.IsNullOrWhiteSpace(value))
                database[key[(key.LastIndexOf(':') + 1)..]] = JsonValue.Create(value);
        }
        var password = SettingsConfigurationKeys.ReadConfiguredValue(configuration, RuntimeSettingKeys.DatabasePassword);
        if (!string.IsNullOrWhiteSpace(password))
        {
            var protector = protection.CreateProtector("MediaPager.Settings.Db.Password.v1");
            database["PasswordProtected"] = JsonValue.Create("enc:v1:" + protector.Protect(password));
        }
        var root = new JsonObject { ["Settings"] = new JsonObject { ["Db"] = database } };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.Move(temporary, path, overwrite: true);
    }

    public static string? ReadProtectedPassword(string path, IDataProtectionProvider protection)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("Settings", out var settings) ||
            !settings.TryGetProperty("Db", out var database) ||
            !database.TryGetProperty("PasswordProtected", out var protectedValue) ||
            protectedValue.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            var value = protectedValue.GetString()!;
            if (value.StartsWith("enc:v1:", StringComparison.Ordinal)) value = value["enc:v1:".Length..];
            return protection.CreateProtector("MediaPager.Settings.Db.Password.v1").Unprotect(value);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The protected database bootstrap password cannot be decrypted.", exception);
        }
    }
}
