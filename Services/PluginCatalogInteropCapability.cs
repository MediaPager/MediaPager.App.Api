using System.Text.Json;
using NoBugsGiven.Quasar.Interop;

namespace MediaPager.App.Api.Services;

/// <summary>Safe Quasar bridge view of the currently loaded plugin catalog. It exposes
/// descriptors/capability names only; settings, secrets, storage paths, and implementation
/// objects are never part of this response.</summary>
public sealed class PluginCatalogInteropCapability(PluginRegistry registry) : IInteropCapability
{
    public InteropCapabilityDescriptor Descriptor { get; } = new(
        "mediapager.plugins.catalog",
        "1.0.0",
        ["list"],
        "Read the public descriptors and declared capabilities of loaded plugins.");

    public ValueTask<JsonElement> InvokeAsync(
        string operation,
        JsonElement arguments,
        InteropInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(operation, "list", StringComparison.Ordinal))
            throw new InteropCapabilityException("mediapager.plugins.catalog.operation", "Unsupported plugin-catalog operation.", 404);

        var plugins = registry.Plugins.Select(plugin => new
        {
            id = plugin.Descriptor.Id,
            name = plugin.Descriptor.Name,
            version = plugin.Descriptor.Version,
            official = !registry.IsCommunity(plugin),
            capabilities = CapabilitiesOf(plugin),
        });
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(plugins));
    }

    private static string[] CapabilitiesOf(IMediaPagerPlugin plugin) => new[]
    {
        plugin is IStreamProviderPlugin ? "stream" : null,
        plugin is IMetadataProviderPlugin ? "metadata" : null,
        plugin is ISearchProviderPlugin ? "search" : null,
        plugin is ISubtitleProviderPlugin ? "subtitles" : null,
        plugin is IEmailProviderPlugin ? "email" : null,
        plugin is IPluginActions ? "actions" : null,
        plugin is IPluginInterface ? "interface" : null,
    }.Where(capability => capability is not null).ToArray()!;
}

public sealed class MediaPagerInteropCapabilityPolicy : IInteropCapabilityPolicy
{
    public ValueTask<bool> IsAllowedAsync(
        System.Security.Claims.ClaimsPrincipal user,
        string capabilityId,
        string operation,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            user.Identity?.IsAuthenticated == true &&
            string.Equals(capabilityId, "mediapager.plugins.catalog", StringComparison.Ordinal) &&
            string.Equals(operation, "list", StringComparison.Ordinal));
}
