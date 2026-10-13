using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MediaPager.App.Api.Services;

/// <summary>
/// The runtime database is authoritative for Settings:Db, but its provider/location must be
/// known before the database can be opened. This data-volume snapshot carries only non-secret
/// locator fields across restarts; passwords remain in the deployment secret environment.
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

    public static void Write(string path, IReadOnlyDictionary<string, string> settings, string? protectedPassword)
    {
        var database = new JsonObject();
        var provider = settings.GetValueOrDefault(RuntimeSettingKeys.DatabaseProvider, "Sqlite");
        foreach (var key in Keys)
        {
            if (key == RuntimeSettingKeys.DatabasePath &&
                !string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                continue;
            if (settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                database[key[(key.LastIndexOf(':') + 1)..]] = JsonValue.Create(value);
        }
        if (!string.IsNullOrWhiteSpace(protectedPassword))
            database["PasswordProtected"] = JsonValue.Create(protectedPassword);
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
