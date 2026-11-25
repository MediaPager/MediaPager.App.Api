using Microsoft.AspNetCore.DataProtection;
using MediaPager.App.Core.Services;

namespace MediaPager.App.Api.Services;

public sealed class PluginDataValueProtector : IPluginDataValueProtector
{
    private readonly IDataProtector _protector;

    public PluginDataValueProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("MediaPager.PluginData.v1");

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string value) => _protector.Unprotect(value);
}
