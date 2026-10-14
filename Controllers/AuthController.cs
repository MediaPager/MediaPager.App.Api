using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public const string MobileSessionTokenProvider = "MediaPager.MobileRefresh";
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan MobileRefreshTokenLifetime = TimeSpan.FromDays(90);

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive || user.DeletedAt is not null || !await users.CheckPasswordAsync(user, request.Password))
            return Unauthorized();

        var (accessToken, expiresAt) = CreateAccessToken(user, DateTime.UtcNow.Add(AccessTokenLifetime));
        return Ok(new { accessToken, tokenType = "Bearer", expiresAt, scopes = user.GetScopes() });
    }

    /// <summary>Exchange credentials once for a revocable, rotating mobile refresh session.</summary>
    [HttpPost("mobile-sessions")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateMobileSession(MobileSessionLoginRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive || user.DeletedAt is not null ||
            !await users.CheckPasswordAsync(user, request.Password))
            return Unauthorized();

        var sessionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var refreshToken = CreateRefreshToken(sessionId);
        var state = new MobileRefreshTokenState(
            HashRefreshSecret(refreshToken.Secret),
            NormalizeDeviceName(request.DeviceName),
            now,
            now,
            now.Add(MobileRefreshTokenLifetime),
            user.SecurityStamp ?? "");

        database.UserTokens.Add(new IdentityUserToken<string>
        {
            UserId = user.Id,
            LoginProvider = MobileSessionTokenProvider,
            Name = sessionId,
            Value = JsonSerializer.Serialize(state),
        });
        await database.SaveChangesAsync(cancellationToken);

        var (accessToken, accessExpiresAt) = CreateAccessToken(
            user,
            DateTime.UtcNow.Add(AccessTokenLifetime),
            mobileSessionId: sessionId);
        return Ok(new MobileSessionTokenResponse(
            accessToken,
            refreshToken.Token,
            sessionId,
            accessExpiresAt,
            state.ExpiresAt,
            user.GetScopes()));
    }

    /// <summary>Rotate the mobile refresh token and issue a new 8-hour access token.</summary>
    [HttpPost("mobile-sessions/refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> RefreshMobileSession(MobileRefreshRequest request, CancellationToken cancellationToken)
    {
        var found = await FindMobileRefreshSessionAsync(request.RefreshToken, cancellationToken);
        if (found is null) return Unauthorized(new { error = "The mobile session expired or was revoked. Sign in again." });

        var (record, state, user, secret) = found.Value;
        if (!RefreshSecretMatches(secret, state.SecretHash)) return Unauthorized();

        if (!user.IsActive || user.DeletedAt is not null ||
            !string.Equals(user.SecurityStamp, state.SecurityStamp, StringComparison.Ordinal))
        {
            database.UserTokens.Remove(record);
            await database.SaveChangesAsync(cancellationToken);
            return Unauthorized(new { error = "The account session is no longer valid. Sign in again." });
        }

        var now = DateTimeOffset.UtcNow;
        var rotatedToken = CreateRefreshToken(record.Name!);
        var rotatedState = state with
        {
            SecretHash = HashRefreshSecret(rotatedToken.Secret),
            LastUsedAt = now,
            ExpiresAt = now.Add(MobileRefreshTokenLifetime),
        };
        record.Value = JsonSerializer.Serialize(rotatedState);
        await database.SaveChangesAsync(cancellationToken);

        var (accessToken, accessExpiresAt) = CreateAccessToken(
            user,
            DateTime.UtcNow.Add(AccessTokenLifetime),
            mobileSessionId: record.Name);
        return Ok(new MobileSessionTokenResponse(
            accessToken,
            rotatedToken.Token,
            record.Name!,
            accessExpiresAt,
            rotatedState.ExpiresAt,
            user.GetScopes()));
    }

    [HttpGet("mobile-sessions")]
    public async Task<IActionResult> GetMobileSessions(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        var records = await database.UserTokens.AsNoTracking()
            .Where(token => token.UserId == userId && token.LoginProvider == MobileSessionTokenProvider)
            .ToListAsync(cancellationToken);
        var currentUser = await users.FindByIdAsync(userId);
        var now = DateTimeOffset.UtcNow;
        var expired = new List<IdentityUserToken<string>>();
        var sessions = new List<MobileSessionDescriptor>();
        var currentSessionId = User.FindFirstValue("mobile_session_id");
        foreach (var record in records)
        {
            var state = DeserializeRefreshState(record.Value);
            if (state is null || state.ExpiresAt <= now || currentUser is null ||
                !string.Equals(state.SecurityStamp, currentUser.SecurityStamp, StringComparison.Ordinal))
            {
                expired.Add(record);
                continue;
            }
            sessions.Add(new MobileSessionDescriptor(
                record.Name!,
                state.DeviceName,
                state.CreatedAt,
                state.LastUsedAt,
                state.ExpiresAt,
                string.Equals(record.Name, currentSessionId, StringComparison.Ordinal)));
        }
        if (expired.Count > 0)
        {
            database.UserTokens.RemoveRange(expired);
            await database.SaveChangesAsync(cancellationToken);
        }
        return Ok(sessions.OrderByDescending(session => session.LastUsedAt));
    }

    /// <summary>Revoke one of the current user's app sessions after confirming their password.</summary>
    [HttpDelete("mobile-sessions/{sessionId}")]
    public async Task<IActionResult> RevokeMobileSession(
        string sessionId,
        RevokeMobileSessionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var user = userId is null ? null : await users.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            !await users.CheckPasswordAsync(user, request.CurrentPassword))
            return Unauthorized(new { error = "The current password was not accepted." });

        var record = await database.UserTokens.SingleOrDefaultAsync(token =>
            token.UserId == user.Id &&
            token.LoginProvider == MobileSessionTokenProvider &&
            token.Name == sessionId, cancellationToken);
        if (record is null) return NotFound();

        database.UserTokens.Remove(record);
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>The app revokes its own session when a saved server connection is removed.</summary>
    [HttpPost("mobile-sessions/revoke")]
    [AllowAnonymous]
    public async Task<IActionResult> RevokeCurrentMobileSession(MobileRefreshRequest request, CancellationToken cancellationToken)
    {
        var found = await FindMobileRefreshSessionAsync(request.RefreshToken, cancellationToken);
        if (found is null) return NoContent();

        var (record, state, _, secret) = found.Value;
        if (!RefreshSecretMatches(secret, state.SecretHash)) return NoContent();
        database.UserTokens.Remove(record);
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
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
        var baseUrlValid = Uri.TryCreate(frontendBaseUrl, UriKind.Absolute, out var frontendUri) && FrontendUrlPolicy.IsAllowed(frontendUri);
        if (!emailConfigured || !baseUrlValid || frontendUri is null)
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
            !FrontendUrlPolicy.IsAllowed(frontendUri))
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

    private (string AccessToken, DateTime ExpiresAt) CreateAccessToken(
        AppUser user,
        DateTime expiresAt,
        string? mobileSessionId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? user.UserName ?? ""),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("security_stamp", user.SecurityStamp ?? ""),
        };
        if (mobileSessionId is not null)
            claims.Add(new Claim("mobile_session_id", mobileSessionId));
        claims.AddRange(user.GetScopes().Select(scope => new Claim("scope", scope)));

        var credentials = new SigningCredentials(tokenConfiguration.SigningKey, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            tokenConfiguration.Issuer,
            tokenConfiguration.Issuer,
            claims,
            expires: expiresAt,
            signingCredentials: credentials);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expiresAt);
    }

    private async Task<(IdentityUserToken<string> Record, MobileRefreshTokenState State, AppUser User, string Secret)?>
        FindMobileRefreshSessionAsync(string? token, CancellationToken cancellationToken)
    {
        var parsed = ParseRefreshToken(token);
        if (parsed is null) return null;

        var record = await database.UserTokens.SingleOrDefaultAsync(item =>
            item.LoginProvider == MobileSessionTokenProvider && item.Name == parsed.Value.SessionId,
            cancellationToken);
        if (record is null) return null;

        var state = DeserializeRefreshState(record.Value);
        var user = await users.FindByIdAsync(record.UserId);
        if (state is null || state.ExpiresAt <= DateTimeOffset.UtcNow || user is null ||
            !user.IsActive || user.DeletedAt is not null ||
            !string.Equals(user.SecurityStamp, state.SecurityStamp, StringComparison.Ordinal))
        {
            database.UserTokens.Remove(record);
            await database.SaveChangesAsync(cancellationToken);
            return null;
        }

        return (record, state, user, parsed.Value.Secret);
    }

    private static (string SessionId, string Secret)? ParseRefreshToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var separator = token.IndexOf('.');
        if (separator <= 0 || separator == token.Length - 1) return null;
        var sessionId = token[..separator];
        var secret = token[(separator + 1)..];
        return Guid.TryParseExact(sessionId, "N", out _) && secret.Length >= 40
            ? (sessionId, secret)
            : null;
    }

    private static (string Token, string Secret) CreateRefreshToken(string sessionId)
    {
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return ($"{sessionId}.{secret}", secret);
    }

    private static string HashRefreshSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static bool RefreshSecretMatches(string secret, string expectedHash)
    {
        try
        {
            var expected = Convert.FromHexString(expectedHash);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static MobileRefreshTokenState? DeserializeRefreshState(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized)) return null;
        try { return JsonSerializer.Deserialize<MobileRefreshTokenState>(serialized); }
        catch (JsonException) { return null; }
    }

    private static string NormalizeDeviceName(string? value)
    {
        var safe = new string((value ?? "MediaPager Flutter").Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (safe.Length == 0) safe = "MediaPager Flutter";
        return safe.Length <= 120 ? safe : safe[..120];
    }

}

public sealed record MobileSessionLoginRequest(string Email, string Password, string? DeviceName = null);
public sealed record MobileRefreshRequest(string RefreshToken);
public sealed record RevokeMobileSessionRequest(string CurrentPassword);
public sealed record MobileSessionTokenResponse(
    string AccessToken,
    string RefreshToken,
    string SessionId,
    DateTime AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt,
    IReadOnlyCollection<string> Scopes);
public sealed record MobileSessionDescriptor(
    string Id,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrentDevice);
public sealed record MobileRefreshTokenState(
    string SecretHash,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset ExpiresAt,
    string SecurityStamp);

public sealed record LoginRequest(string Email, string Password);
public sealed record EmailRequest(string Email);
public sealed record ResetPasswordRequest(string Email, string Token, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record InviteRegisterRequest(string Email, string Token, string Password);
public sealed record CreateInviteRequest(string Email);
