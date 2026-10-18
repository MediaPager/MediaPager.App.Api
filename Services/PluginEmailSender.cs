using System.Net.Mail;
using System.Text;
using MediaPager.App.PluginContracts;
using Microsoft.Extensions.DependencyInjection;

namespace MediaPager.App.Api.Services;

public sealed class PluginEmailSender(IServiceScopeFactory scopeFactory) : IPluginEmailSender
{
    private const int MaxRecipientLength = 320;
    private const int MaxSubjectLength = 998;
    private const int MaxBodyBytes = 1_000_000;

    public async Task<bool> SendAsync(PluginEmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message);

        await using var scope = scopeFactory.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<EmailSender>();
        return await sender.SendAsync(
            message.Recipient.Trim(), message.Subject, message.Text, message.Html, cancellationToken);
    }

    private static void Validate(PluginEmailMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Recipient) || message.Recipient.Length > MaxRecipientLength ||
            !MailAddress.TryCreate(message.Recipient, out _))
            throw InvalidMessage("A valid recipient address is required.");

        if (string.IsNullOrWhiteSpace(message.Subject) || message.Subject.Length > MaxSubjectLength ||
            message.Subject.Contains('\r') || message.Subject.Contains('\n'))
            throw InvalidMessage("A valid subject is required.");

        if (message.Text is null || message.Html is null ||
            Encoding.UTF8.GetByteCount(message.Text) > MaxBodyBytes ||
            Encoding.UTF8.GetByteCount(message.Html) > MaxBodyBytes ||
            (string.IsNullOrWhiteSpace(message.Text) && string.IsNullOrWhiteSpace(message.Html)))
            throw InvalidMessage("The email body is missing or too large.");
    }

    private static PluginOperationException InvalidMessage(string message) =>
        new(new PluginError("plugin.email.invalid_message", message));
}
