using MediaPager.App.PluginContracts;

namespace MediaPager.App.Api.Services;

/// <summary>
/// Sends host mail (password resets, invitations) through whichever email provider plugin
/// is active. The host knows nothing about Mailgun/SMTP/Gmail specifics — each plugin owns
/// its settings schema under plugins.{key}.* and the registry supplies the candidates.
/// Resolution order: Email:Provider (full plugin id, or a legacy short value like
/// "mailgun") → first configured provider → first installed one. With no email plugin
/// loaded there is nothing to send through and auth flows degrade to "not configured".
/// </summary>
public sealed class EmailSender(
    PluginRegistry registry,
    IRuntimeSettings runtimeSettings,
    ILogger<EmailSender> logger)
{
    public async Task<IEmailProviderPlugin?> ProviderAsync(CancellationToken cancellationToken = default)
    {
        var providers = registry.ResolveAll<IEmailProviderPlugin>();
        if (providers.Count == 0) return null;

        var wanted = (await runtimeSettings.GetAsync(RuntimeSettingKeys.EmailProvider, cancellationToken))?.Trim();
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            // Match the full plugin id ("mediapager.email.smtp") or its last segment —
            // which keeps legacy short values ("smtp", "mailgun", "gmail", "office365") working.
            var match = providers.FirstOrDefault(provider =>
                    string.Equals(provider.Descriptor.Id, wanted, StringComparison.OrdinalIgnoreCase)) ??
                providers.FirstOrDefault(provider =>
                    string.Equals(provider.Descriptor.Id.Split('.')[^1], wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        foreach (var provider in providers)
        {
            if (await provider.IsConfiguredAsync(cancellationToken)) return provider;
        }

        return providers[0];
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
        await ProviderAsync(cancellationToken) is { } provider && await provider.IsConfiguredAsync(cancellationToken);

    public async Task<bool> SendAsync(string recipient, string subject, string text, string html,
        CancellationToken cancellationToken = default)
    {
        var provider = await ProviderAsync(cancellationToken);
        if (provider is null)
        {
            logger.LogWarning("No email provider plugin is loaded — cannot send '{Subject}'.", subject);
            return false;
        }

        var sent = await provider.SendAsync(recipient, subject, text, html, cancellationToken);
        if (!sent)
            logger.LogError("Email provider {PluginId} failed to send '{Subject}'.", provider.Descriptor.Id, subject);
        return sent;
    }
}
