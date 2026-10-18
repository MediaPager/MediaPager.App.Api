using MediaPager.App.Api.Services;
using MediaPager.App.Api.Services.Workers;
using MediaPager.App.Core.Plugins;
using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class PluginEmailSenderTests
{
    [Fact]
    public async Task SendAsyncUsesTheConfiguredHostEmailProvider()
    {
        var provider = new RecordingEmailProvider();
        var registry = new PluginRegistry();
        registry.Reset([provider]);
        var emailSender = new EmailSender(registry, new EmptyRuntimeSettings(), NullLogger<EmailSender>.Instance);
        var services = new ServiceCollection();
        services.AddScoped(_ => emailSender);
        using var serviceProvider = services.BuildServiceProvider();
        var pluginSender = new PluginEmailSender(serviceProvider.GetRequiredService<IServiceScopeFactory>());

        var sent = await pluginSender.SendAsync(new PluginEmailMessage(
            "reader@example.test", "Completed", "Plain text", "<p>HTML</p>"));

        Assert.True(sent);
        Assert.Equal("reader@example.test", provider.Recipient);
        Assert.Equal("Completed", provider.Subject);
        Assert.Equal("Plain text", provider.Text);
        Assert.Equal("<p>HTML</p>", provider.Html);
    }

    [Fact]
    public async Task SendAsyncRejectsHeaderInjectionBeforeCreatingAScope()
    {
        var scopeFactory = new ThrowingScopeFactory();
        var pluginSender = new PluginEmailSender(scopeFactory);

        await Assert.ThrowsAsync<PluginOperationException>(() => pluginSender.SendAsync(
            new PluginEmailMessage("reader@example.test", "Hello\r\nBcc: other@example.test", "Body", "<p>Body</p>")));
        Assert.False(scopeFactory.ScopeWasCreated);
    }

    [Fact]
    public void ManifestAcceptsTheEmailSendingCapability()
    {
        const string json = """{"id":"mediapager.actions.sample","name":"Sample","description":"","author":"Tests","types":"actions","sdk":"MediaPager.App.PluginContracts","sdkVersion":"0.1.0","hostCapabilities":["email-sending"]}""";

        Assert.True(PluginManifestFile.TryParse(json, out var manifest, out var error), error);
        Assert.Contains(PluginHostCapabilityNames.EmailSending, manifest!.HostCapabilities!);
    }

    [Fact]
    public void WorkerEmailServiceIsOnlyAvailableWhenManifestCapabilityIsGranted()
    {
        var host = new PluginWorkerHostClient(new StringReader(string.Empty), new StringWriter());
        var withoutCapability = new PluginWorkerServiceProvider(
            host, "mediapager.actions.example", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var withCapability = new PluginWorkerServiceProvider(
            host,
            "mediapager.actions.example",
            new HashSet<string>([PluginHostCapabilityNames.EmailSending], StringComparer.OrdinalIgnoreCase));

        Assert.Null(withoutCapability.GetService(typeof(IPluginEmailSender)));
        Assert.False(withoutCapability.IsService(typeof(IPluginEmailSender)));
        Assert.IsAssignableFrom<IPluginEmailSender>(withCapability.GetService(typeof(IPluginEmailSender)));
        Assert.True(withCapability.IsService(typeof(IPluginEmailSender)));
    }

    private sealed class RecordingEmailProvider : IEmailProviderPlugin
    {
        public PluginDescriptor Descriptor { get; } = new(
            "mediapager.email.test", "Test", "1.0.0", "Tests", "Test provider.");
        public string? Recipient { get; private set; }
        public string? Subject { get; private set; }
        public string? Text { get; private set; }
        public string? Html { get; private set; }

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SendAsync(
            string recipient,
            string subject,
            string text,
            string html,
            CancellationToken cancellationToken = default)
        {
            Recipient = recipient;
            Subject = subject;
            Text = text;
            Html = html;
            return Task.FromResult(true);
        }
    }

    private sealed class EmptyRuntimeSettings : IRuntimeSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task SetAllAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeedMissingFromConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveDatabaseSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public bool ScopeWasCreated { get; private set; }

        public IServiceScope CreateScope()
        {
            ScopeWasCreated = true;
            throw new InvalidOperationException("A scope should not be created for invalid input.");
        }
    }
}
