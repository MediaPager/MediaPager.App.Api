namespace MediaPager.App.Api.Services.Workers;

/// <summary>Provides the deployed plugin folder for a host-side proxy whose assembly lives
/// in the API process but whose packaged UI assets belong to the worker plugin.</summary>
internal interface IPluginUiAssetProvider
{
    string PluginUiDirectory { get; }
}
