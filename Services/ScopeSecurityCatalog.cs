using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaPager.App.Api.Services;

public sealed record ScopeSecurityDefinition(
    string Id,
    string Claim,
    string Label,
    string Description,
    IReadOnlySet<string> Permissions);

/// <summary>Loads the configured scope catalog. Scope names are display/assignment data;
/// authorization is granted only through the permission identifiers recognized by the host.</summary>
public sealed class ScopeSecurityCatalog
{
    private static readonly Regex PascalBoundary = new("([a-z0-9])([A-Z])", RegexOptions.Compiled);
    private readonly IReadOnlyDictionary<string, ScopeSecurityDefinition> _scopes;

    private ScopeSecurityCatalog(IReadOnlyDictionary<string, ScopeSecurityDefinition> scopes) => _scopes = scopes;

    public IReadOnlyList<ScopeSecurityDefinition> Scopes => _scopes.Values.ToArray();

    public static ScopeSecurityCatalog Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The configured scope-security file was not found.", path);
        var source = StripComments(File.ReadAllText(path));
        using var document = JsonDocument.Parse(source, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The scope-security file must contain a JSON object grouped by scope category.");

        var scopes = new Dictionary<string, ScopeSecurityDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.RootElement.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Scope group '{group.Name}' must be an array.");
            foreach (var item in group.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException($"Scope entries in '{group.Name}' require a string 'id'.");
                var rawId = idValue.GetString()!;
                var claim = NormalizeScope(group.Name, rawId);
                var description = item.TryGetProperty("desc", out var descValue) && descValue.ValueKind == JsonValueKind.String
                    ? descValue.GetString() ?? ""
                    : item.TryGetProperty("description", out descValue) && descValue.ValueKind == JsonValueKind.String
                        ? descValue.GetString() ?? ""
                        : "";
                var permissions = item.TryGetProperty("permissions", out var permissionsValue) &&
                                  permissionsValue.ValueKind == JsonValueKind.Array
                    ? permissionsValue.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString()!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var label = item.TryGetProperty("label", out var labelValue) && labelValue.ValueKind == JsonValueKind.String
                    ? labelValue.GetString() ?? rawId
                    : rawId;
                if (!scopes.TryAdd(claim, new ScopeSecurityDefinition(rawId, claim, label, description, permissions)))
                    throw new InvalidOperationException($"Duplicate scope '{claim}' in the scope-security file.");
            }
        }
        if (!scopes.ContainsKey("admin:super"))
            throw new InvalidOperationException("The scope-security file must define Admin:Super.");
        return new ScopeSecurityCatalog(scopes);
    }

    public bool IsDefined(string claim) => _scopes.ContainsKey(CanonicalizeLegacy(claim));

    public IReadOnlyList<ScopeSecurityDefinition> ScopesFor(IEnumerable<IMediaPagerPlugin> plugins)
    {
        var scopes = _scopes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins.Where(candidate => candidate is IPluginScopeSchema))
        {
            var schema = (IPluginScopeSchema)plugin;
            var namespaceId = plugin.Descriptor.Id.Split('.')[^1].ToLowerInvariant();
            foreach (var definition in schema.Scopes)
            {
                var localId = ToKebab(definition.Id);
                if (localId.Length == 0 || localId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
                    throw new InvalidOperationException($"Plugin '{plugin.Descriptor.Id}' declares an invalid scope id '{definition.Id}'.");
                var claim = $"{namespaceId}:{localId}";
                if (!scopes.TryAdd(claim, new ScopeSecurityDefinition(
                        definition.Id, claim, definition.Label, definition.Description,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase))))
                    throw new InvalidOperationException($"Plugin '{plugin.Descriptor.Id}' declares duplicate scope '{claim}'.");
            }
        }
        return scopes.Values.ToArray();
    }

    public bool IsDefined(string claim, IEnumerable<IMediaPagerPlugin> plugins) =>
        ScopesFor(plugins).Any(scope => string.Equals(scope.Claim, CanonicalizeLegacy(claim), StringComparison.OrdinalIgnoreCase));

    public string? PluginScopeClaim(IMediaPagerPlugin plugin, string localScopeId)
    {
        var namespaceId = plugin.Descriptor.Id.Split('.')[^1].ToLowerInvariant();
        var normalizedId = ToKebab(localScopeId);
        var claim = $"{namespaceId}:{normalizedId}";
        return plugin is IPluginScopeSchema schema && schema.Scopes.Any(scope =>
            string.Equals(ToKebab(scope.Id), normalizedId, StringComparison.OrdinalIgnoreCase))
            ? claim
            : null;
    }

    public bool HasPermission(IEnumerable<string> claims, string permission)
    {
        var normalized = claims.Select(CanonicalizeLegacy).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalized.Contains("admin:super")) return true;
        return normalized.Any(claim => _scopes.TryGetValue(claim, out var scope) && scope.Permissions.Contains(permission));
    }

    public static string NormalizeScope(string group, string id)
    {
        var groupSlug = ToKebab(group);
        var idSlug = ToKebab(id);
        if (groupSlug.Length == 0 || idSlug.Length == 0 ||
            groupSlug.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')) ||
            idSlug.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new InvalidOperationException($"Scope group/id '{group}:{id}' contains unsupported characters.");
        return $"{groupSlug}:{idSlug}";
    }

    public static string CanonicalizeLegacy(string claim) =>
        string.Equals(claim, "admin:can-invite", StringComparison.OrdinalIgnoreCase)
            ? "users:can-invite"
            : claim.ToLowerInvariant();

    private static string ToKebab(string value) =>
        PascalBoundary.Replace(value.Trim(), "$1-$2").Replace('_', '-').ToLowerInvariant();

    private static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var inString = false;
        var escaped = false;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            if (inString)
            {
                result.Append(current);
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
                continue;
            }
            if (current == '"')
            {
                inString = true;
                result.Append(current);
                continue;
            }
            if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not ('\r' or '\n')) index++;
                if (index < source.Length) result.Append(source[index]);
                continue;
            }
            if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                var closed = false;
                while (index < source.Length)
                {
                    if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/')
                    {
                        index++;
                        closed = true;
                        break;
                    }
                    if (source[index] is '\r' or '\n') result.Append(source[index]);
                    index++;
                }
                if (!closed) throw new InvalidOperationException("The scope-security file contains an unterminated block comment.");
                continue;
            }
            result.Append(current);
        }
        return result.ToString();
    }
}
