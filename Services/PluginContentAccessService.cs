using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MediaPager.App.Api.Controllers;
using MediaPager.App.Core.Models;
using MediaPager.App.Core.Plugins;
using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;

namespace MediaPager.App.Api.Services;

/// <summary>Enforces plugin-declared scopes, optional plugin-wide visibility settings, and
/// the current user's mature-content preference at host-owned content boundaries.</summary>
public sealed class PluginContentAccessService(
    PluginRegistry registry,
    ScopeSecurityCatalog scopeCatalog,
    IPluginSettingsRepository pluginSettings,
    AuthDbContext database)
{
    public async Task<bool> CanAccessAsync(
        IMediaPagerPlugin plugin,
        string? localScopeId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localScopeId)) return true;

        var requiredClaim = scopeCatalog.PluginScopeClaim(plugin, localScopeId);
        if (requiredClaim is null) return false;

        var userScopes = user.FindAll("scope")
            .Select(claim => ScopeSecurityCatalog.CanonicalizeLegacy(claim.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = userScopes.Contains("admin:super") || userScopes.Contains(requiredClaim);

        if (!allowed)
        {
            var scope = scopeCatalog.ScopesFor(registry.Plugins).FirstOrDefault(candidate =>
                string.Equals(candidate.Claim, requiredClaim, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(scope?.GlobalAccessSettingKey))
            {
                var value = await pluginSettings.GetAsync(
                    plugin.Descriptor.Id, scope.GlobalAccessSettingKey, cancellationToken);
                allowed = bool.TryParse(value, out var globallyEnabled) && globallyEnabled;
            }
        }

        if (!allowed) return false;
        return !await IsScopeHiddenByUserPreferenceAsync(plugin, localScopeId, user, cancellationToken);
    }

    private async Task<bool> IsScopeHiddenByUserPreferenceAsync(
        IMediaPagerPlugin plugin,
        string localScopeId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var preference = plugin is IPluginUserSettingsSchema schema
            ? schema.UserSettings.FirstOrDefault(setting =>
                string.Equals(setting.HidesScope, localScopeId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (preference is null) return false;

        var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrWhiteSpace(userId)) return preference.DefaultValue;
        var preferenceKey = UserSettingsController.PluginUserSettingKey(plugin.Descriptor.Id, preference.Key);

        var value = await database.UserSettings
            .AsNoTracking()
            .Where(setting => setting.UserId == userId && setting.Key == preferenceKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return value is null
            ? preference.DefaultValue
            : bool.TryParse(value, out var hide) ? hide : preference.DefaultValue;
    }
}
