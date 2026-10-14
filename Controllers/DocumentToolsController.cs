using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using System.Text.RegularExpressions;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("document-tools")]
[Authorize]
public sealed class DocumentToolsController(DocumentToolsetInstaller tools) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status() => Ok(tools.GetStatus());

    [HttpPost("check-updates")]
    [Authorize(Policy = "CanEditSettings")]
    public async Task<IActionResult> CheckUpdates(CancellationToken cancellationToken) =>
        Ok(await tools.CheckForUpdatesAsync(cancellationToken));

    [HttpPost("update")]
    [Authorize(Policy = "CanEditSettings")]
    public async Task<IActionResult> Update(CancellationToken cancellationToken) =>
        Accepted(await tools.UpdateToLatestAsync(cancellationToken));
}

[ApiController]
public sealed class DocumentToolAssetsController(DocumentToolsetInstaller tools) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly Regex LinkTag = new("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PreloadRelation = new("""\brel\s*=\s*['"]preload['"]""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FetchDestination = new("""\bas\s*=\s*['"]fetch['"]""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IntegrityAttribute = new("""\s+integrity\s*=\s*(?:'[^']*'|"[^"]*")""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [HttpGet("/tools/docs/{toolId}/{**assetPath}")]
    [AllowAnonymous]
    public async Task<IActionResult> Asset(string toolId, string? assetPath, CancellationToken cancellationToken)
    {
        var fullPath = tools.ResolveAssetPath(toolId, assetPath);
        if (fullPath is null) return NotFound();

        if (string.Equals(Path.GetFileName(fullPath), "index.html", StringComparison.OrdinalIgnoreCase))
        {
            var html = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken);
            Response.Headers.CacheControl = "no-store";
            return Content(RemoveUnsupportedPreloadIntegrity(html), "text/html");
        }

        if (!ContentTypes.TryGetContentType(fullPath, out var contentType))
            contentType = Path.GetExtension(fullPath).Equals(".wasm", StringComparison.OrdinalIgnoreCase)
                ? "application/wasm"
                : "application/octet-stream";
        return PhysicalFile(fullPath, contentType);
    }

    private static string RemoveUnsupportedPreloadIntegrity(string html) => LinkTag.Replace(html, match =>
    {
        var tag = match.Value;
        return PreloadRelation.IsMatch(tag) && FetchDestination.IsMatch(tag)
            ? IntegrityAttribute.Replace(tag, string.Empty)
            : tag;
    });
}
