using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("me")]
public sealed class MeController(
    UserManager<AppUser> users,
    AuthDbContext database,
    EmailSender emailSender,
    ILogger<MeController> logger) : ControllerBase
{
    private async Task<AppUser?> CurrentUserAsync(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return userId is null ? null : await users.FindByIdAsync(userId);
    }

    [HttpGet]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync(cancellationToken);
        return user is null ? Unauthorized() : Ok(ToProfile(user));
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();

        var firstName = request.FirstName?.Trim();
        var lastName = request.LastName?.Trim();
        if (firstName?.Length > 60) return BadRequest(new { error = "First name must be 60 characters or fewer." });
        if (lastName?.Length > 60) return BadRequest(new { error = "Last name must be 60 characters or fewer." });

        user.FirstName = string.IsNullOrEmpty(firstName) ? null : firstName;
        user.LastName = string.IsNullOrEmpty(lastName) ? null : lastName;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(ToProfile(user));
    }

    [HttpPost("email")]
    public async Task<IActionResult> RequestEmailChange(RequestEmailChangeRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();

        if (!MailAddress.TryCreate(request.NewEmail?.Trim(), out var address))
            return BadRequest(new { error = "Enter a valid email address." });
        var newEmail = address.Address;
        if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "That is already the email on this account." });
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || !await users.CheckPasswordAsync(user, request.CurrentPassword))
            return BadRequest(new { error = "Your current password is required to change the email." });
        if (await users.FindByEmailAsync(newEmail) is not null)
            return Conflict(new { error = "An account already exists for that email." });
        if (!await emailSender.IsConfiguredAsync(cancellationToken))
            return Problem("An email provider must be configured to verify the new address.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        user.PendingEmail = newEmail;
        user.PendingEmailCodeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        user.PendingEmailExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        await database.SaveChangesAsync(cancellationToken);

        var sent = await emailSender.SendAsync(newEmail, "Verify your MediaPager email change",
            $"Your verification code is {code}. Enter it in MediaPager to change the account email to this address. It expires in 15 minutes.",
            $"<p>Your verification code is <strong>{code}</strong>.</p><p>Enter it in MediaPager to change the account email to this address. It expires in 15 minutes.</p>",
            cancellationToken);
        if (!sent)
        {
            ClearPendingEmailChange(user);
            await database.SaveChangesAsync(cancellationToken);
            logger.LogError("Email change verification mail failed to send to {Email}.", newEmail);
            return Problem("The verification email could not be sent.", statusCode: StatusCodes.Status502BadGateway);
        }

        return Ok(new { message = $"Verification code sent to {newEmail}. It expires in 15 minutes." });
    }

    [HttpPost("email/confirm")]
    public async Task<IActionResult> ConfirmEmailChange(ConfirmEmailChangeRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();

        var code = request.Code?.Trim();
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "Enter the verification code." });
        if (user.PendingEmail is null || user.PendingEmailCodeHash is null || user.PendingEmailExpiresAt is null)
            return BadRequest(new { error = "No email change is pending. Start a change from your profile." });

        if (user.PendingEmailExpiresAt <= DateTimeOffset.UtcNow)
        {
            ClearPendingEmailChange(user);
            await database.SaveChangesAsync(cancellationToken);
            return BadRequest(new { error = "That verification code has expired. Request a new one." });
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        if (!string.Equals(hash, user.PendingEmailCodeHash, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "That verification code is incorrect." });

        var newEmail = user.PendingEmail;
        // Re-check at confirm time: the request-time lookup can go stale while a code is pending.
        var existing = await users.FindByEmailAsync(newEmail);
        if (existing is not null && existing.Id != user.Id)
            return Conflict(new { error = "An account already exists for that email." });
        // Write identity fields directly instead of UserManager.SetEmailAsync/SetUserNameAsync:
        // those regenerate the security stamp, and the per-request stamp check in Program.cs
        // would 401 the confirming session on its very next call.
        user.Email = newEmail;
        user.NormalizedEmail = users.NormalizeEmail(newEmail);
        user.UserName = newEmail;
        user.NormalizedUserName = users.NormalizeName(newEmail);
        user.EmailConfirmed = true;
        ClearPendingEmailChange(user);
        await database.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Email changed.", profile = ToProfile(user) });
    }

    private static void ClearPendingEmailChange(AppUser user)
    {
        user.PendingEmail = null;
        user.PendingEmailCodeHash = null;
        user.PendingEmailExpiresAt = null;
    }

    private static object ToProfile(AppUser user) => new
    {
        id = user.Id,
        userName = user.UserName,
        email = user.Email,
        emailConfirmed = user.EmailConfirmed,
        firstName = user.FirstName,
        lastName = user.LastName,
        scopes = user.GetScopes(),
    };
}

public sealed record UpdateProfileRequest(string? FirstName, string? LastName);
public sealed record RequestEmailChangeRequest(string? NewEmail, string? CurrentPassword);
public sealed record ConfirmEmailChangeRequest(string? Code);