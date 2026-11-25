using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

// Per-user preferences (such as playback behavior and plugin-declared options). Personal, not admin-scoped:
// like the catalog nav order, any authenticated user reads/writes their own rows.
[ApiController]
[Route("me")]
public sealed class UserSettingsController(AuthDbContext database, PluginRegistry registry) : ControllerBase
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "autoplay",
    };

    internal static string PluginUserSettingKey(string pluginId, string key) => $"plugin:{pluginId}:{key}";

    // GET /me/settings — the current user's preferences as a flat { key: value } map.
    [HttpGet("settings")]
    public async Task<IActionResult> GetUserSettings(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        var rows = await database.UserSettings
            .AsNoTracking()
            .Where(setting => setting.UserId == userId)
            .ToListAsync(cancellationToken);
        var settings = rows.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal);
        foreach (var plugin in registry.Plugins.OfType<IPluginUserSettingsSchema>())
        {
            var pluginId = ((IMediaPagerPlugin)plugin).Descriptor.Id;
            foreach (var preference in plugin.UserSettings)
            {
                var key = PluginUserSettingKey(pluginId, preference.Key);
                if (!settings.ContainsKey(key))
                    settings[key] = preference.DefaultValue ? "true" : "false";
            }
        }

        return Ok(new { settings });
    }

    // PUT /me/settings — replace-all update of the user's preferences. Only known
    // keys are accepted; the payload can carry one or several at once.
    [HttpPut("settings")]
    public async Task<IActionResult> PutUserSettings(UserSettingsRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        var pluginPreferences = registry.Plugins
            .Where(plugin => plugin is IPluginUserSettingsSchema)
            .SelectMany(plugin => ((IPluginUserSettingsSchema)plugin).UserSettings
                .Select(preference => PluginUserSettingKey(plugin.Descriptor.Id, preference.Key)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in request.Settings.Keys)
        {
            if (!KnownKeys.Contains(key) && !pluginPreferences.Contains(key))
                return BadRequest(new { error = $"Unknown setting '{key}'." });
        }
        if (request.Settings.TryGetValue("autoplay", out var autoplay) && autoplay is not ("true" or "false"))
            return BadRequest(new { error = "Setting 'autoplay' must be true or false." });
        foreach (var (key, value) in request.Settings)
            if (pluginPreferences.Contains(key) && value is not ("true" or "false"))
                return BadRequest(new { error = $"Setting '{key}' must be true or false." });

        var existing = await database.UserSettings
            .Where(setting => setting.UserId == userId && request.Settings.Keys.Contains(setting.Key))
            .ToListAsync(cancellationToken);

        foreach (var (key, value) in request.Settings)
        {
            var row = existing.FirstOrDefault(setting => setting.Key == key);
            if (row is null)
                database.UserSettings.Add(new UserSetting { UserId = userId, Key = key, Value = value });
            else
                row.Value = value;
        }
        await database.SaveChangesAsync(cancellationToken);

        return Ok(new { settings = request.Settings });
    }
}

public sealed record UserSettingsRequest(Dictionary<string, string> Settings);
