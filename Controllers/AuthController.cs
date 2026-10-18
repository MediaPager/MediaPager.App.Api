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
        var resetUrl = BuildFrontendAuthUrl(frontendUri, "reset", address.Address, resetToken);
        var logoUrl = BuildFrontendAssetUrl(frontendUri, "mediapager-email-logo.png");
        var safeResetUrl = HtmlEncoder.Default.Encode(resetUrl);
        var safeLogoUrl = HtmlEncoder.Default.Encode(logoUrl);
        var resetText = $"""
            MEDIA PAGER  |  ACCOUNT SECURITY

            Let’s get you back in.

            Hi {address.Address},

            We received a request to reset the password for your MediaPager account. Use your private link below to choose a new one.

            RESET MY PASSWORD
            {resetUrl}

            If you didn’t request a reset, you can ignore this email. Your password won’t change unless you use the link.

            — The MediaPager team
            """;
        var resetHtml = $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <meta name="x-apple-disable-message-reformatting">
                <meta name="color-scheme" content="dark">
                <meta name="supported-color-schemes" content="dark">
                <title>Your MediaPager password reset link</title>
            </head>
            <body style="margin:0;padding:0;background:#070b14;font-family:Arial,Helvetica,sans-serif;color:#edf3fb">
                <div style="display:none;font-size:1px;line-height:1px;color:#070b14;max-height:0;max-width:0;opacity:0;overflow:hidden">A private link to reset your MediaPager password.</div>
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" bgcolor="#070b14" style="background:#070b14;padding:38px 12px">
                    <tr><td align="center">
                        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="max-width:600px;background:#0e1525;border:1px solid #263348;border-radius:16px;overflow:hidden">
                            <tr><td style="padding:0">
                                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0"><tr>
                                    <td width="42%" height="4" bgcolor="#12d9f5" style="height:4px;background:#12d9f5;font-size:0;line-height:0">&nbsp;</td>
                                    <td width="33%" height="4" bgcolor="#1686ff" style="height:4px;background:#1686ff;font-size:0;line-height:0">&nbsp;</td>
                                    <td width="25%" height="4" bgcolor="#86d411" style="height:4px;background:#86d411;font-size:0;line-height:0">&nbsp;</td>
                                </tr></table>
                            </td></tr>
                            <tr><td style="padding:30px 38px 0">
                                <table role="presentation" cellspacing="0" cellpadding="0" border="0"><tr>
                                    <td width="52" height="52" align="center" valign="middle"><img src="{{safeLogoUrl}}" width="52" height="52" alt="MediaPager" style="display:block;width:52px;height:52px;border:1px solid #263d58;border-radius:12px"></td>
                                    <td style="padding-left:12px;color:#f1f5f9;font-size:14px;font-weight:bold;letter-spacing:2px">MEDIAPAGER</td>
                                </tr></table>
                            </td></tr>
                            <tr><td style="padding:32px 38px 38px">
                                <div style="display:inline-block;padding:7px 10px;border:1px solid #34532b;border-radius:999px;background:#142218;color:#a6e858;font-size:10px;font-weight:bold;letter-spacing:1.5px">ACCOUNT SECURITY</div>
                                <h1 style="margin:18px 0 12px;color:#f7f9fc;font-size:32px;line-height:1.16;letter-spacing:-0.6px">Let’s get you back in.</h1>
                                <p style="margin:0;color:#b8c5d8;font-size:16px;line-height:1.7">Hi {{HtmlEncoder.Default.Encode(address.Address)}}, we received a request to reset the password for your MediaPager account. Use your private link below to choose a new one.</p>
                                <table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin-top:26px"><tr>
                                    <td align="center" bgcolor="#86d411" style="border-radius:9px"><a href="{{safeResetUrl}}" style="display:inline-block;padding:15px 24px;color:#10170a;font-size:14px;font-weight:bold;text-decoration:none">Reset my password&nbsp;&nbsp; &#8594;</a></td>
                                </tr></table>
                                <p style="margin:25px 0 8px;color:#8fa2ba;font-size:12px;line-height:1.6">Button not working? Copy this private reset link into your browser:</p>
                                <p style="margin:0;padding:12px 14px;background:#090f1b;border:1px solid #1e2a3d;border-radius:8px;color:#70cfff;font-size:12px;line-height:1.6;word-break:break-all"><a href="{{safeResetUrl}}" style="color:#70cfff;text-decoration:underline">{{safeResetUrl}}</a></p>
                                <p style="margin:22px 0 0;color:#8fa2ba;font-size:12px;line-height:1.7">If you didn’t request a reset, you can ignore this email. Your password won’t change unless you use the link.</p>
                            </td></tr>
                            <tr><td style="padding:0 38px"><div style="height:1px;background:#202d41;font-size:0;line-height:0">&nbsp;</div></td></tr>
                            <tr><td style="padding:19px 38px 25px;color:#697b94;font-size:11px;line-height:1.6">Sent by MediaPager · Keeping your library secure.</td></tr>
                        </table>
                    </td></tr>
                </table>
            </body>
            </html>
            """;
        var sent = await emailSender.SendAsync(address.Address, "Your MediaPager password reset link",
            resetText, resetHtml,
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

        var inviteUrl = BuildFrontendAuthUrl(frontendUri, "register", address.Address, token);
        var logoUrl = BuildFrontendAssetUrl(frontendUri, "mediapager-email-logo.png");
        var safeInviteUrl = HtmlEncoder.Default.Encode(inviteUrl);
        var safeInviteEmail = HtmlEncoder.Default.Encode(address.Address);
        var safeLogoUrl = HtmlEncoder.Default.Encode(logoUrl);
        var inviteText = $"""
            MEDIA PAGER  |  YOU'RE INVITED

            Your next movie night starts here.

            Hi {address.Address},

            You’ve been invited to join a shared MediaPager library. Accept the invitation below to create your account and start watching together.

            JOIN THE LIBRARY
            {inviteUrl}

            INVITATION DETAILS
            Invited account: {address.Address}
            Link expires: 24 hours from when it was sent

            This invitation is private to you and can only be used once. If you weren’t expecting it, you can ignore this message. No account will be created unless you accept.

            — The MediaPager team
            """;
        var inviteHtml = $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <meta name="x-apple-disable-message-reformatting">
                <meta name="color-scheme" content="dark">
                <meta name="supported-color-schemes" content="dark">
                <title>A MediaPager library is waiting for you</title>
            </head>
            <body style="margin:0;padding:0;background:#070b14;font-family:Arial,Helvetica,sans-serif;color:#edf3fb">
                <div style="display:none;font-size:1px;line-height:1px;color:#070b14;max-height:0;max-width:0;opacity:0;overflow:hidden">You’ve been invited to join a shared MediaPager library. Your invitation expires in 24 hours.</div>
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" bgcolor="#070b14" style="background:#070b14;padding:38px 12px">
                    <tr><td align="center">
                        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="max-width:600px;background:#0e1525;border:1px solid #263348;border-radius:16px;overflow:hidden">
                            <tr><td style="padding:0">
                                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0"><tr>
                                    <td width="42%" height="4" bgcolor="#12d9f5" style="height:4px;background:#12d9f5;font-size:0;line-height:0">&nbsp;</td>
                                    <td width="33%" height="4" bgcolor="#1686ff" style="height:4px;background:#1686ff;font-size:0;line-height:0">&nbsp;</td>
                                    <td width="25%" height="4" bgcolor="#86d411" style="height:4px;background:#86d411;font-size:0;line-height:0">&nbsp;</td>
                                </tr></table>
                            </td></tr>
                            <tr><td style="padding:30px 38px 0">
                                <table role="presentation" cellspacing="0" cellpadding="0" border="0"><tr>
                                    <td width="52" height="52" align="center" valign="middle"><img src="{{safeLogoUrl}}" width="52" height="52" alt="MediaPager" style="display:block;width:52px;height:52px;border:1px solid #263d58;border-radius:12px"></td>
                                    <td style="padding-left:12px;color:#f1f5f9;font-size:14px;font-weight:bold;letter-spacing:2px">MEDIAPAGER</td>
                                </tr></table>
                            </td></tr>
                            <tr><td style="padding:32px 38px 38px">
                                <div style="display:inline-block;padding:7px 10px;border:1px solid #34532b;border-radius:999px;background:#142218;color:#a6e858;font-size:10px;font-weight:bold;letter-spacing:1.5px">YOU'RE INVITED</div>
                                <h1 style="margin:18px 0 12px;color:#f7f9fc;font-size:32px;line-height:1.16;letter-spacing:-0.6px">Your next movie night starts here.</h1>
                                <p style="margin:0;color:#b8c5d8;font-size:16px;line-height:1.7">Hi {{safeInviteEmail}}, you’ve been invited to join a shared MediaPager library. Accept your invitation to create an account and start watching together.</p>
                                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="margin-top:26px;background:#111d30;border:1px solid #24364e;border-radius:12px">
                                    <tr><td style="padding:17px 19px 15px">
                                        <div style="color:#8fa2ba;font-size:10px;font-weight:bold;letter-spacing:1.4px">INVITATION FOR</div>
                                        <div style="padding-top:6px;color:#edf3fb;font-size:14px;font-weight:bold;word-break:break-word">{{safeInviteEmail}}</div>
                                    </td><td align="right" style="padding:17px 19px 15px">
                                        <div style="color:#8fa2ba;font-size:10px;font-weight:bold;letter-spacing:1.4px">EXPIRES IN</div>
                                        <div style="padding-top:6px;color:#a6e858;font-size:14px;font-weight:bold">24 hours</div>
                                    </td></tr>
                                </table>
                                <table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin-top:25px"><tr>
                                    <td align="center" bgcolor="#86d411" style="border-radius:9px;box-shadow:0 5px 18px rgba(134,212,17,.18)"><a href="{{safeInviteUrl}}" style="display:inline-block;padding:15px 24px;color:#10170a;font-size:14px;font-weight:bold;text-decoration:none;letter-spacing:.2px">Join the library&nbsp;&nbsp; &#8594;</a></td>
                                </tr></table>
                                <p style="margin:25px 0 8px;color:#8fa2ba;font-size:12px;line-height:1.6">Button not working? Copy this private invitation link into your browser:</p>
                                <p style="margin:0;padding:12px 14px;background:#090f1b;border:1px solid #1e2a3d;border-radius:8px;color:#70cfff;font-size:12px;line-height:1.6;word-break:break-all"><a href="{{safeInviteUrl}}" style="color:#70cfff;text-decoration:underline">{{safeInviteUrl}}</a></p>
                                <p style="margin:22px 0 0;color:#8fa2ba;font-size:12px;line-height:1.7">This link is private to you and can only be used once. If you weren’t expecting this invite, you can ignore this email—no account will be created unless you accept.</p>
                            </td></tr>
                            <tr><td style="padding:0 38px"><div style="height:1px;background:#202d41;font-size:0;line-height:0">&nbsp;</div></td></tr>
                            <tr><td style="padding:19px 38px 25px;color:#697b94;font-size:11px;line-height:1.6">Sent by MediaPager · Your library, your way.</td></tr>
                        </table>
                    </td></tr>
                </table>
            </body>
            </html>
            """;
        var sent = await emailSender.SendAsync(address.Address, "A MediaPager library is waiting for you",
            inviteText, inviteHtml, cancellationToken);
        if (!sent)
        {
            database.RegistrationInvites.Remove(invite);
            await database.SaveChangesAsync(cancellationToken);
            loggerFactory.CreateLogger("Invitations").LogError("The configured email provider failed to send an invitation to {Email}.", address.Address);
            return Problem("The invitation email could not be sent.", statusCode: StatusCodes.Status502BadGateway);
        }

        return Ok(new { message = "Invitation sent. The link expires in 24 hours." });
    }

    private static string BuildFrontendAuthUrl(Uri frontendUri, string mode, string email, string token)
    {
        // Keep the configured path: deployments often serve the SPA below a reverse-proxy
        // prefix such as https://example.com/mediapager/.
        var pageUrl = frontendUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return $"{pageUrl}?mode={Uri.EscapeDataString(mode)}" +
            $"&email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    private static string BuildFrontendAssetUrl(Uri frontendUri, string assetName) =>
        $"{frontendUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{Uri.EscapeDataString(assetName)}";

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
