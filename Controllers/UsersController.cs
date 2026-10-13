using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("users")]
[Authorize(Policy = "SuperAdmin")]
public sealed class UsersController(
    UserManager<AppUser> users,
    ScopeSecurityCatalog scopeCatalog,
    PluginRegistry registry) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await users.Users.AsNoTracking().OrderBy(user => user.Email).ToListAsync(cancellationToken);
        return Ok(result.Select(user => new
        {
            id = user.Id,
            email = user.Email ?? user.UserName ?? "",
            firstName = user.FirstName,
            lastName = user.LastName,
            isActive = user.IsActive,
            deletedAt = user.DeletedAt,
            emailConfirmed = user.EmailConfirmed,
            status = user.DeletedAt is not null ? "deleted" : user.IsActive ? "active" : "inactive",
            scopes = user.GetScopes(),
        }));
    }

    [HttpGet("scopes")]
    public IActionResult Scopes() => Ok(scopeCatalog.ScopesFor(registry.Plugins).Select(scope => new
    {
        id = scope.Claim,
        label = scope.Label,
        description = scope.Description,
    }));

    [HttpPut("{id}/scopes")]
    public async Task<IActionResult> UpdateScopes(string id, UpdateUserScopesRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound(new { error = "User not found." });
        if (user.DeletedAt is not null) return Conflict(new { error = "Restore this user before changing scopes." });

        var requested = (request.Scopes ?? []).Select(scope => scope?.Trim() ?? "")
            .Where(scope => scope.Length > 0)
            .Select(ScopeSecurityCatalog.CanonicalizeLegacy)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var definedScopes = scopeCatalog.ScopesFor(registry.Plugins);
        var unknown = requested.Where(scope => !scopeCatalog.IsDefined(scope, registry.Plugins)).ToArray();
        if (unknown.Length > 0)
            return BadRequest(new { error = "One or more scopes are not supported.", scopes = unknown });

        var wasSuperAdmin = user.GetScopes().Contains("admin:super", StringComparer.Ordinal);
        var remainsSuperAdmin = requested.Contains("admin:super", StringComparer.Ordinal);
        if (wasSuperAdmin && !remainsSuperAdmin && IsCurrentUser(id) && user.IsActive)
            return Conflict(new { error = "You cannot remove your own super-administrator access." });
        if (wasSuperAdmin && !remainsSuperAdmin && user.IsActive &&
            !await HasOtherActiveSuperAdminAsync(id, cancellationToken))
            return Conflict(new { error = "The last active super-administrator must remain active." });

        user.Scopes = string.Join(' ', definedScopes.Select(scope => scope.Claim).Where(requested.Contains));
        await users.UpdateSecurityStampAsync(user);
        return Ok(ToItem(user));
    }

    [HttpPost("{id}/deactivate")]
    public Task<IActionResult> Deactivate(string id, CancellationToken cancellationToken) =>
        SetActiveAsync(id, active: false, cancellationToken);

    [HttpPost("{id}/activate")]
    public Task<IActionResult> Activate(string id, CancellationToken cancellationToken) =>
        SetActiveAsync(id, active: true, cancellationToken);

    [HttpDelete("{id}")]
    public async Task<IActionResult> SoftDelete(string id, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound(new { error = "User not found." });
        if (IsCurrentUser(id)) return Conflict(new { error = "You cannot delete your own account from user management." });
        if (user.DeletedAt is not null) return Ok(ToItem(user));
        if (user.IsActive && user.GetScopes().Contains("admin:super", StringComparer.Ordinal) &&
            !await HasOtherActiveSuperAdminAsync(id, cancellationToken))
            return Conflict(new { error = "The last active super-administrator must remain active." });

        user.DeletedAt = DateTimeOffset.UtcNow;
        await users.UpdateSecurityStampAsync(user);
        return Ok(ToItem(user));
    }

    [HttpPost("{id}/restore")]
    public async Task<IActionResult> Restore(string id, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound(new { error = "User not found." });
        if (user.DeletedAt is null) return Ok(ToItem(user));
        user.DeletedAt = null;
        await users.UpdateSecurityStampAsync(user);
        return Ok(ToItem(user));
    }

    private async Task<IActionResult> SetActiveAsync(string id, bool active, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound(new { error = "User not found." });
        if (user.DeletedAt is not null) return Conflict(new { error = "Restore this user before changing their active status." });
        if (!active && IsCurrentUser(id)) return Conflict(new { error = "You cannot deactivate your own account." });
        if (!active && user.IsActive && user.GetScopes().Contains("admin:super", StringComparer.Ordinal) &&
            !await HasOtherActiveSuperAdminAsync(id, cancellationToken))
            return Conflict(new { error = "The last active super-administrator must remain active." });

        if (user.IsActive != active)
        {
            user.IsActive = active;
            await users.UpdateSecurityStampAsync(user);
        }
        return Ok(ToItem(user));
    }

    private async Task<bool> HasOtherActiveSuperAdminAsync(string userId, CancellationToken cancellationToken)
    {
        var activeUsers = await users.Users.AsNoTracking()
            .Where(user => user.Id != userId && user.IsActive && user.DeletedAt == null)
            .Select(user => user.Scopes)
            .ToListAsync(cancellationToken);
        return activeUsers.Any(scopes => scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("admin:super", StringComparer.Ordinal));
    }

    private bool IsCurrentUser(string id) =>
        string.Equals(User.FindFirstValue(JwtRegisteredClaimNames.Sub), id, StringComparison.Ordinal);

    private static object ToItem(AppUser user) => new
    {
        id = user.Id,
        email = user.Email ?? user.UserName ?? "",
        firstName = user.FirstName,
        lastName = user.LastName,
        isActive = user.IsActive,
        deletedAt = user.DeletedAt,
        emailConfirmed = user.EmailConfirmed,
        status = user.DeletedAt is not null ? "deleted" : user.IsActive ? "active" : "inactive",
        scopes = user.GetScopes(),
    };

}

public sealed record UpdateUserScopesRequest(IReadOnlyList<string>? Scopes);
