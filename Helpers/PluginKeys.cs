namespace MediaPager.App.Api.Helpers;

/// <summary>
/// Plugin settings keys for host code that reads plugin-scoped values directly. The
/// plugin itself reads the same IPluginSettingsStore; key = last segment of the
/// plugin's descriptor id.
/// </summary>
public static class PluginKeys
{
    public const string Tmdb = "tmdb";
}
