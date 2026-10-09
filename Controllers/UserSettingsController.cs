using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

// Per-user preferences (user settings like TV autoplay). Personal, not admin-scoped:
// like the catalog nav order, any authenticated user reads/writes their own rows.
[ApiController]
[Route("me")]
public sealed class UserSettingsController(AuthDbContext database) : ControllerBase
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal) { "autoplay" };

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

        return Ok(new { settings });
    }

    // PUT /me/settings — replace-all update of the user's preferences. Only known
    // keys are accepted; the payload can carry one or several at once.
    [HttpPut("settings")]
    public async Task<IActionResult> PutUserSettings(UserSettingsRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        foreach (var key in request.Settings.Keys)
        {
            if (!KnownKeys.Contains(key))
                return BadRequest(new { error = $"Unknown setting '{key}'." });
        }
        if (request.Settings.TryGetValue("autoplay", out var autoplay) && autoplay is not ("true" or "false"))
            return BadRequest(new { error = "Setting 'autoplay' must be true or false." });

        var existing = await database.UserSettings
            .Where(setting => setting.UserId == userId && KnownKeys.Contains(setting.Key))
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