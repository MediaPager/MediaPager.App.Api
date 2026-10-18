using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("me")]
public sealed class MeController(
    UserManager<AppUser> users,
    AuthDbContext database,
    EmailSender emailSender,
    IRuntimeSettings runtimeSettings,
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

        var configuredSiteUrl = await runtimeSettings.GetAsync(RuntimeSettingKeys.FrontendBaseUrl, cancellationToken);
        var logoUrl = Uri.TryCreate(configuredSiteUrl, UriKind.Absolute, out var siteUri) && FrontendUrlPolicy.IsAllowed(siteUri)
            ? $"{siteUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/mediapager-email-logo.png"
            : null;
        var safeLogoUrl = logoUrl is null ? null : HtmlEncoder.Default.Encode(logoUrl);
        var logoMarkup = safeLogoUrl is null
            ? "<span style=\"display:inline-block;padding:11px 13px;background:#14243a;border:1px solid #263d58;border-radius:12px;color:#86d411;font-size:19px;font-weight:bold\">&#9654;</span>"
            : $"<img src=\"{safeLogoUrl}\" width=\"52\" height=\"52\" alt=\"MediaPager\" style=\"display:block;width:52px;height:52px;border:1px solid #263d58;border-radius:12px\">";
        var safeEmail = HtmlEncoder.Default.Encode(newEmail);
        var verificationText = $"""
            MEDIA PAGER  |  ACCOUNT SECURITY

            CONFIRM YOUR EMAIL CHANGE

            Hi {newEmail},

            Enter this code in MediaPager to confirm the new email address for your account:

                {code}

            This code expires in 15 minutes and can only be used once.

            If you didn’t request this change, you can ignore this email. Your account email will stay the same.

            — The MediaPager team
            """;
        var verificationHtml = $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <meta name="x-apple-disable-message-reformatting">
                <meta name="color-scheme" content="dark">
                <meta name="supported-color-schemes" content="dark">
                <title>Confirm your MediaPager email change</title>
            </head>
            <body style="margin:0;padding:0;background:#070b14;font-family:Arial,Helvetica,sans-serif;color:#edf3fb">
                <div style="display:none;font-size:1px;line-height:1px;color:#070b14;max-height:0;max-width:0;opacity:0;overflow:hidden">Confirm the new email address for your MediaPager account.</div>
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
                                    <td width="52" height="52" align="center" valign="middle">{{logoMarkup}}</td>
                                    <td style="padding-left:12px;color:#f1f5f9;font-size:14px;font-weight:bold;letter-spacing:2px">MEDIAPAGER</td>
                                </tr></table>
                            </td></tr>
                            <tr><td style="padding:32px 38px 38px">
                                <div style="display:inline-block;padding:7px 10px;border:1px solid #34532b;border-radius:999px;background:#142218;color:#a6e858;font-size:10px;font-weight:bold;letter-spacing:1.5px">ACCOUNT SECURITY</div>
                                <h1 style="margin:18px 0 12px;color:#f7f9fc;font-size:32px;line-height:1.16;letter-spacing:-0.6px">Confirm your email change.</h1>
                                <p style="margin:0;color:#b8c5d8;font-size:16px;line-height:1.7">Hi {{safeEmail}}, enter this code in MediaPager to confirm the new email address for your account.</p>
                                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="margin-top:26px;background:#111d30;border:1px solid #24364e;border-radius:12px">
                                    <tr><td align="center" style="padding:22px 18px 20px">
                                        <div style="color:#8fa2ba;font-size:10px;font-weight:bold;letter-spacing:1.5px">YOUR CONFIRMATION CODE</div>
                                        <div style="padding-top:12px;color:#a6e858;font-size:34px;font-weight:bold;letter-spacing:10px">{{code}}</div>
                                    </td></tr>
                                </table>
                                <p style="margin:22px 0 0;color:#8fa2ba;font-size:13px;line-height:1.7">This code expires in <strong style="color:#edf3fb">15 minutes</strong> and can only be used once.</p>
                                <p style="margin:12px 0 0;color:#8fa2ba;font-size:12px;line-height:1.7">If you didn’t request this change, you can ignore this email. Your account email will stay the same.</p>
                            </td></tr>
                            <tr><td style="padding:0 38px"><div style="height:1px;background:#202d41;font-size:0;line-height:0">&nbsp;</div></td></tr>
                            <tr><td style="padding:19px 38px 25px;color:#697b94;font-size:11px;line-height:1.6">Sent by MediaPager · Keeping your library secure.</td></tr>
                        </table>
                    </td></tr>
                </table>
            </body>
            </html>
            """;
        var sent = await emailSender.SendAsync(newEmail, "Confirm your MediaPager email change",
            verificationText, verificationHtml,
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
