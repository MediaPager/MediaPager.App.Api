using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace MediaPager.App.Api.Controllers;

public sealed record AuthTokenConfiguration(string Issuer, SymmetricSecurityKey SigningKey);

[ApiController]
[Route("auth")]
public sealed class AuthController(
    UserManager<AppUser> users,
    AuthDbContext database,
    EmailSender emailSender,
    IRuntimeSettings runtimeSettings,
    AuthTokenConfiguration tokenConfiguration,
    ILoggerFactory loggerFactory) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !await users.CheckPasswordAsync(user, request.Password))
            return Unauthorized();

        var expiresAt = DateTime.UtcNow.AddHours(8);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? request.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("security_stamp", user.SecurityStamp ?? ""),
        }.Concat(user.GetScopes().Select(scope => new Claim("scope", scope)));
        var credentials = new SigningCredentials(tokenConfiguration.SigningKey, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(tokenConfiguration.Issuer, tokenConfiguration.Issuer, claims,
            expires: expiresAt, signingCredentials: credentials);
        var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);
        return Ok(new { accessToken, tokenType = "Bearer", expiresAt, scopes = user.GetScopes() });
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(EmailRequest request, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("PasswordReset");
        if (!MailAddress.TryCreate(request.Email?.Trim(), out var address))
            return BadRequest(new { error = "Enter a valid email address." });

        // TEMP DEBUG: 'debug' field explains why the email was/wasn't sent. Revert before production.
        const string genericText = "If an account exists for that email, a password reset link will be sent.";
        var frontendBaseUrl = await runtimeSettings.GetAsync(RuntimeSettingKeys.FrontendBaseUrl, cancellationToken);
        var emailConfigured = await emailSender.IsConfiguredAsync(cancellationToken);
        var baseUrlValid = Uri.TryCreate(frontendBaseUrl, UriKind.Absolute, out var frontendUri) && IsAllowedFrontendUri(frontendUri);
        if (!emailConfigured || !baseUrlValid)
        {
            logger.LogWarning("Password reset requested but the email provider or Frontend:BaseUrl is not fully configured.");
            return Ok(new { message = genericText, sent = false, debug =
                !emailConfigured
                    ? "Email provider not configured (pick one in Settings → Email and fill in its settings)."
                    : $"Frontend:BaseUrl '{frontendBaseUrl}' is missing or not an allowed https/LAN URL." });
        }

        var user = await users.FindByEmailAsync(address.Address);
        if (user is null)
            return Ok(new { message = genericText, sent = false, debug = $"No account found for {address.Address}." });

        var resetToken = await users.GeneratePasswordResetTokenAsync(user);
        var resetUrl = $"{frontendUri.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/?mode=reset" +
            $"&email={Uri.EscapeDataString(address.Address)}&token={Uri.EscapeDataString(resetToken)}";
        var safeResetUrl = HtmlEncoder.Default.Encode(resetUrl);
        var sent = await emailSender.SendAsync(address.Address, "Reset your MediaPager password",
            $"Use this link to reset your MediaPager password. It expires shortly.\n\n{resetUrl}",
            $"<p>Use this link to reset your MediaPager password. It expires shortly.</p><p><a href=\"{safeResetUrl}\">Reset password</a></p>",
            cancellationToken);
        if (!sent)
        {
            logger.LogError("Unable to send password reset email through the configured provider.");
            return Ok(new { message = genericText, sent = false, debug = "Provider configured but SendAsync failed (check API logs / provider configuration)." });
        }

        return Ok(new { message = genericText, sent = true, debug = $"Reset email handed to the configured provider for {address.Address}." });
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(InviteRegisterRequest request, CancellationToken cancellationToken)
    {
        if (!MailAddress.TryCreate(request.Email?.Trim(), out var address))
            return BadRequest(new { error = "Enter a valid email address." });
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "An invitation token and password are required." });

        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var invite = await database.RegistrationInvites.SingleOrDefaultAsync(
            item => item.TokenHash == tokenHash, cancellationToken);
        if (invite is null || invite.UsedAt is not null || invite.ExpiresAt <= DateTimeOffset.UtcNow ||
            !string.Equals(invite.Email, address.Address, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "The invitation link is invalid or expired." });

        if (await users.FindByEmailAsync(address.Address) is not null)
            return BadRequest(new { error = "An account already exists for this email." });

        var user = new AppUser
        {
            UserName = address.Address,
            Email = address.Address,
            EmailConfirmed = true,
        };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(error => error.Description) });

        invite.UsedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { message = "Account created. Sign in to continue." });
    }

    [HttpPost("invites")]
    [Authorize(Policy = "CanInvite")]
    public async Task<IActionResult> CreateInvite(CreateInviteRequest request, CancellationToken cancellationToken)
    {
        if (!MailAddress.TryCreate(request.Email?.Trim(), out var address))
            return BadRequest(new { error = "Enter a valid email address." });
        if (!await emailSender.IsConfiguredAsync(cancellationToken) ||
            !Uri.TryCreate(await runtimeSettings.GetAsync(RuntimeSettingKeys.FrontendBaseUrl, cancellationToken), UriKind.Absolute, out var frontendUri) ||
            !IsAllowedFrontendUri(frontendUri))
            return Problem("An email provider and Frontend:BaseUrl must be configured to send invitations.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if (await users.FindByEmailAsync(address.Address) is not null)
            return Conflict(new { error = "An account already exists for this email." });

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var invite = new RegistrationInvite
        {
            Email = address.Address,
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            CreatedByUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub)!,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
        };
        database.RegistrationInvites.Add(invite);
        await database.SaveChangesAsync(cancellationToken);

        var inviteUrl = $"{frontendUri.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/?mode=register" +
            $"&email={Uri.EscapeDataString(address.Address)}&token={Uri.EscapeDataString(token)}";
        var safeInviteUrl = HtmlEncoder.Default.Encode(inviteUrl);
        var sent = await emailSender.SendAsync(address.Address, "Your MediaPager invitation",
            $"You have been invited to MediaPager. This link expires in 24 hours.\n\n{inviteUrl}",
            $"<p>You have been invited to MediaPager. This link expires in 24 hours.</p><p><a href=\"{safeInviteUrl}\">Accept invitation</a></p>",
            cancellationToken);
        if (!sent)
        {
            database.RegistrationInvites.Remove(invite);
            await database.SaveChangesAsync(cancellationToken);
            loggerFactory.CreateLogger("Invitations").LogError("The configured email provider failed to send an invitation to {Email}.", address.Address);
            return Problem("The invitation email could not be sent.", statusCode: StatusCodes.Status502BadGateway);
        }

        return Ok(new { message = "Invitation sent. The link expires in 24 hours." });
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!MailAddress.TryCreate(request.Email?.Trim(), out var address))
            return BadRequest(new { error = "Enter a valid email address." });
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "A reset token and new password are required." });

        var user = await users.FindByEmailAsync(address.Address);
        if (user is null) return BadRequest(new { error = "The reset link is invalid or expired." });

        var result = await users.ResetPasswordAsync(user, request.Token, request.Password);
        if (!result.Succeeded)
            return BadRequest(new
            {
                errors = result.Errors.Select(error => error.Description),
            });

        await users.UpdateSecurityStampAsync(user);
        return Ok(new { message = "Password reset. Sign in with your new password." });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { error = "Your current password and a new password are required." });

        var userId = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        var user = userId is null ? null : await users.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(error => error.Description) });

        // Invalidate other sessions; this request's token stays valid until it expires.
        await users.UpdateSecurityStampAsync(user);
        return Ok(new { message = "Password changed. Other sessions have been signed out." });
    }

    // Allow https for any host, and http for loopback/localhost and private LAN addresses
    // (RFC1918: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16) so LAN deployments work without TLS.
    private static bool IsAllowedFrontendUri(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (uri.Scheme != Uri.UriSchemeHttp) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(uri.Host, out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168);
    }
}

public sealed record LoginRequest(string Email, string Password);
public sealed record EmailRequest(string Email);
public sealed record ResetPasswordRequest(string Email, string Token, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record InviteRegisterRequest(string Email, string Token, string Password);
public sealed record CreateInviteRequest(string Email);
