using Microsoft.AspNetCore.DataProtection;

namespace MediaPager.App.Api.Services;

public sealed class PluginSettingsValueProtector : IPluginSettingsValueProtector
{
    private readonly IDataProtector _protector;

    public PluginSettingsValueProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("MediaPager.PluginSettings.v1");

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string value) => _protector.Unprotect(value);
}
