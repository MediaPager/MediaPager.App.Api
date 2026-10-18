using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaPager.App.Api.Services.Workers;

internal static class PluginWorkerProtocol
{
    public const int CurrentVersion = 1;
}

internal sealed record PluginWorkerMessage(
    string Type,
    string Id,
    string? Operation = null,
    string? Capability = null,
    JsonElement? Arguments = null,
    JsonElement? Result = null,
    bool Success = true,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    PluginWorkerDescription? Description = null,
    int ProtocolVersion = PluginWorkerProtocol.CurrentVersion);

internal sealed record PluginWorkerDescription(
    PluginDescriptor Descriptor,
    bool IsStreamProvider,
    bool HasSettingsSchema,
    bool HasActions,
    bool HasNavigation,
    SourceDescriptor[] Sources,
    StreamProviderMode Mode,
    MediaKind[] SupportedKinds,
    PluginSettingDefinition[] Settings,
    PluginActionDescriptor[] Actions,
    PluginSettingsNav? SettingsNav,
    PluginMenuEntry[] MainNav,
    PluginScopeDefinition[] Scopes);

internal sealed record PluginWorkerResolveArguments(StreamResolveRequest Request);
internal sealed record PluginWorkerBrowseArguments(string SourceKey, string? Query, int Page);
internal sealed record PluginWorkerDetailsArguments(string SourceKey, string ExternalId);
internal sealed record PluginWorkerActionArguments(PluginActionContext Context);
internal sealed record PluginWorkerNotifyArguments(string Title, string? Message, PluginNotificationLevel Level);
internal sealed record PluginWorkerNotifyErrorArguments(string Title, PluginError Error, PluginNotificationLevel Level);
internal sealed record PluginWorkerJobArguments(string JobId, string? Status = null, double? Progress = null, PluginError? Error = null);
internal sealed record PluginWorkerActivityBeginJobArguments(string Title);
internal sealed record PluginWorkerActivityClearArguments(string JobId);
internal sealed record PluginWorkerJobHandle(string Id);
internal sealed record PluginWorkerTransferArguments(MediaTransferRequest Request);
internal sealed record PluginWorkerEmailArguments(PluginEmailMessage Message);
internal sealed record PluginWorkerDestinationArguments(int CatalogId, string CatalogTypeSlug);
internal sealed record PluginWorkerActionPresenceArguments(string PluginId, MediaKind Kind);
internal sealed record PluginWorkerMediaKindArguments(MediaKind Kind);
internal sealed record PluginWorkerSettingsArguments(string PluginKey, string Name, string? Value = null);
internal sealed record PluginWorkerMediaStreamArguments(PluginActionContext Context);
internal sealed record PluginWorkerStreamResolution(Uri? UpstreamUri, MediaKind? Kind);

internal static class PluginWorkerJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T Deserialize<T>(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default!;
        return value.Value.Deserialize<T>(Options)!;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
