namespace MediaPager.App.Api.Helpers;

/// <summary>
/// Canonical plugin IDs for host code that reads plugin-scoped values directly. The
/// plugin itself receives the same namespaced view through IPluginSettingsStore.
/// </summary>
public static class PluginKeys
{
    public const string Tmdb = "mediapager.metadata.tmdb";
}
