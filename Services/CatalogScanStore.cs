using Microsoft.EntityFrameworkCore;
using MediaPager.App.Core.Services;

namespace MediaPager.App.Api.Services;

/// <summary>Host-backed implementation of the <c>catalog-scanning</c> plugin service. It owns
/// no database context of its own: each operation opens a short scope so in-process plugins
/// (which are singletons) can hold this service without capturing a scoped DbContext.</summary>
public sealed class CatalogScanStore(IServiceScopeFactory scopeFactory) : ICatalogScanStore
{
    private static readonly string[] KnownTagTypes =
        ["genre", "country", "writer", "director", "producer", "tags"];

    // Windows paths are case-insensitive; Unix (including macOS) containment is checked
    // case-sensitively, which is the conservative choice for mixed-case volumes.
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<IReadOnlyList<CatalogScanCatalogInfo>> ListCatalogsAsync(CancellationToken cancellationToken = default) =>
        await WithDatabaseAsync(async (database, token) =>
        {
            var catalogs = await database.Catalogs.AsNoTracking()
                .Include(catalog => catalog.CatalogType)
                .OrderBy(catalog => catalog.Id)
                .ToListAsync(token);
            return (IReadOnlyList<CatalogScanCatalogInfo>)catalogs.Select(ToCatalogInfo).ToList();
        }, cancellationToken);

    public async Task<CatalogScanCatalogInfo?> GetCatalogAsync(int catalogId, CancellationToken cancellationToken = default) =>
        await WithDatabaseAsync(async (database, token) =>
        {
            var catalog = await database.Catalogs.AsNoTracking()
                .Include(entry => entry.CatalogType)
                .FirstOrDefaultAsync(entry => entry.Id == catalogId, token);
            return catalog is null ? null : ToCatalogInfo(catalog);
        }, cancellationToken);

    public async Task<IReadOnlyList<CatalogScanItemInfo>> ListItemsAsync(int catalogId, CancellationToken cancellationToken = default) =>
        await WithDatabaseAsync(async (database, token) =>
        {
            var catalog = await database.Catalogs.AsNoTracking()
                .Include(entry => entry.CatalogType)
                .FirstOrDefaultAsync(entry => entry.Id == catalogId, token);
            if (catalog is null) return (IReadOnlyList<CatalogScanItemInfo>)[];

            var items = await database.CatalogItems.AsNoTracking()
                .Where(item => item.CatalogId == catalogId)
                .OrderBy(item => item.Id)
                .ToListAsync(token);
            var result = new List<CatalogScanItemInfo>(items.Count);
            foreach (var item in items)
            {
                // A row whose stored path has drifted outside the folder is not exposed to a
                // plugin. Writes/delete fail loudly on the same condition (see EnsureStoredPath).
                if (item.StoragePath is not null && !IsContained(catalog.Path, item.StoragePath)) continue;
                result.Add(ToItemInfo(item));
            }
            return result;
        }, cancellationToken);

    public async Task<CatalogScanItemInfo?> FindByPathAsync(string path, CancellationToken cancellationToken = default) =>
        await WithDatabaseAsync(async (database, token) =>
        {
            var catalogs = await database.Catalogs.AsNoTracking()
                .Where(catalog => catalog.Path != null && catalog.Path != "")
                .OrderByDescending(catalog => catalog.Path!.Length)
                .ToListAsync(token);
            foreach (var catalog in catalogs)
            {
                if (!IsContained(catalog.Path, path)) continue;
                string resolved;
                try { resolved = ResolvePath(path); }
                catch (UnauthorizedAccessException) { continue; }

                var item = await database.CatalogItems.AsNoTracking()
                    .Where(entry => entry.CatalogId == catalog.Id && entry.StoragePath != null)
                    .ToListAsync(token);
                var match = item.FirstOrDefault(entry =>
                    string.Equals(TryResolvePath(entry.StoragePath!), resolved, PathComparison));
                if (match is not null) return ToItemInfo(match);
            }
            return null;
        }, cancellationToken);

    public async Task<int> CreateAsync(CatalogScanCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("An item title is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.StoragePath))
            throw new ArgumentException("A storage path is required.", nameof(request));

        return await WithDatabaseAsync(async (database, token) =>
        {
            var catalog = await database.Catalogs
                .Include(entry => entry.CatalogType)
                .FirstOrDefaultAsync(entry => entry.Id == request.CatalogId, token)
                ?? throw new KeyNotFoundException($"Catalog '{request.CatalogId}' does not exist.");
            if (catalog.CatalogType is null)
                throw new InvalidOperationException($"Catalog '{request.CatalogId}' has no catalog type.");

            var storagePath = EnsureContained(catalog.Path, request.StoragePath);
            var storedPaths = await database.CatalogItems.AsNoTracking()
                .Where(item => item.StoragePath != null)
                .Select(item => item.StoragePath!)
                .ToListAsync(token);
            if (storedPaths.Any(existing =>
                    string.Equals(TryResolvePath(existing), storagePath, PathComparison)))
                throw new InvalidOperationException(
                    $"An item with storage path '{request.StoragePath}' already exists.");

            var item = new CatalogItem
            {
                CatalogId = catalog.Id,
                MediaTypeId = catalog.CatalogType.MediaTypeId,
                Title = request.Title.Trim(),
                Kind = string.IsNullOrWhiteSpace(request.Kind) ? "item" : request.Kind.Trim(),
                StoragePath = storagePath,
                SizeBytes = request.SizeBytes,
            };
            ApplyScanUpdate(item, request.Scan);
            database.CatalogItems.Add(item);
            await database.SaveChangesAsync(token);

            if (request.Scan?.Metadata?.Tags is { } tags)
            {
                await ReplaceTagsAsync(database, item.Id, tags, token);
                await database.SaveChangesAsync(token);
            }
            return item.Id;
        }, cancellationToken);
    }

    public async Task UpdateAsync(int itemId, CatalogScanIdentityUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await WithDatabaseAsync(async (database, token) =>
        {
            var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == itemId, token)
                ?? throw new KeyNotFoundException($"Catalog item '{itemId}' does not exist.");
            await EnsureStoredPathContainedAsync(database, item, token);

            ApplyScanUpdate(item, update);
            item.ModifiedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(token);

            if (update.Metadata?.Tags is { } tags)
            {
                await ReplaceTagsAsync(database, itemId, tags, token);
                await database.SaveChangesAsync(token);
            }
            return true;
        }, cancellationToken);
    }

    public async Task DeleteAsync(int itemId, CancellationToken cancellationToken = default) =>
        await WithDatabaseAsync(async (database, token) =>
        {
            var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == itemId, token);
            if (item is null) return false;
            await EnsureStoredPathContainedAsync(database, item, token);

            var candidateIds = await database.CatalogItemMusicArtistCredits.AsNoTracking()
                .Where(credit => credit.CatalogItemId == itemId)
                .Select(credit => credit.ArtistCatalogItemId)
                .ToListAsync(token);
            if (item.ParentId is int parentId) candidateIds.Add(parentId);

            // Only the database row goes away — a scanner must never delete a media file.
            database.CatalogItems.Remove(item);
            await database.SaveChangesAsync(token);
            await MusicCatalogHierarchy.PruneEmptyAncestorsAsync(database, candidateIds, token);
            return true;
        }, cancellationToken);

    private static CatalogScanCatalogInfo ToCatalogInfo(Catalog catalog) => new(
        catalog.Id,
        catalog.CatalogTypeId,
        catalog.CatalogType?.Slug ?? "unknown",
        catalog.Name,
        catalog.Path);

    private static CatalogScanItemInfo ToItemInfo(CatalogItem item) => new(
        item.Id,
        item.CatalogId,
        item.Kind,
        item.Title,
        item.StoragePath,
        item.SizeBytes,
        item.ExternalId,
        item.Fingerprint,
        item.Confidence,
        item.ScanSignature,
        item.LastScannedAt,
        item.LastSeenAt);

    private static void ApplyScanUpdate(CatalogItem item, CatalogScanIdentityUpdate? update)
    {
        if (update is null) return;
        if (update.Fingerprint is not null) item.Fingerprint = update.Fingerprint;
        if (update.Confidence is not null) item.Confidence = update.Confidence;
        if (update.ScanSignature is not null) item.ScanSignature = update.ScanSignature;
        if (update.LastScannedAt is not null) item.LastScannedAt = update.LastScannedAt;
        if (update.LastSeenAt is not null) item.LastSeenAt = update.LastSeenAt;

        if (update.Metadata is not { } metadata) return;
        if (metadata.Title is not null) item.Title = metadata.Title.Trim();
        if (metadata.Year is not null) item.Year = metadata.Year;
        if (metadata.Overview is not null) item.Overview = metadata.Overview;
        if (metadata.ImageUrl is not null) item.ImageUrl = metadata.ImageUrl;
        if (metadata.BackdropUrl is not null) item.BackdropUrl = metadata.BackdropUrl;
        if (metadata.ExternalId is not null) item.ExternalId = metadata.ExternalId;
        if (metadata.Rating is not null) item.Rating = metadata.Rating;
    }

    private static async Task ReplaceTagsAsync(
        AuthDbContext database,
        int itemId,
        IReadOnlyList<CatalogScanTag> tags,
        CancellationToken cancellationToken)
    {
        var existing = await database.CatalogItemTags
            .Where(tag => tag.CatalogItemId == itemId)
            .ToListAsync(cancellationToken);
        database.CatalogItemTags.RemoveRange(existing);

        var seen = new HashSet<(string Type, string Value)>();
        foreach (var tag in tags)
        {
            var type = (tag.Type ?? "").Trim().ToLowerInvariant();
            if (!KnownTagTypes.Contains(type))
                throw new ArgumentException($"Unknown tag type '{tag.Type}'.", nameof(tags));
            var value = (tag.Value ?? "").Trim();
            if (value.Length == 0) continue;
            if (value.Length > 200)
                throw new ArgumentException($"Tag value '{value}' is too long (max 200 characters).", nameof(tags));
            if (!seen.Add((type, value))) continue;
            database.CatalogItemTags.Add(new CatalogItemTag
            {
                CatalogItemId = itemId,
                Type = type,
                Value = value,
            });
        }
    }

    private async Task EnsureStoredPathContainedAsync(
        AuthDbContext database,
        CatalogItem item,
        CancellationToken cancellationToken)
    {
        if (item.StoragePath is null) return;
        var catalog = await database.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == item.CatalogId, cancellationToken)
            ?? throw new InvalidOperationException($"Catalog item '{item.Id}' has no owning catalog.");
        EnsureContained(catalog.Path, item.StoragePath);
    }

    private static bool IsContained(string? catalogPath, string candidatePath)
    {
        try
        {
            _ = EnsureContained(catalogPath, candidatePath);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Normalizes a scan path and resolves any link component. A resolved path is what
    /// we store, so a symlink that leaves the catalog can never be pointed at later.</summary>
    private static string EnsureContained(string? catalogPath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(catalogPath))
            throw new InvalidOperationException(
                "The catalog has no folder; scanned items must belong to a folder-backed catalog.");
        if (string.IsNullOrWhiteSpace(candidatePath))
            throw new ArgumentException("A storage path is required.", nameof(candidatePath));

        var root = ResolvePath(catalogPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = ResolvePath(candidatePath);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison) || candidate.Length == prefix.Length)
            throw new UnauthorizedAccessException(
                $"The path '{candidatePath}' is outside catalog folder '{catalogPath}'.");
        return candidate;
    }

    private static string TryResolvePath(string path)
    {
        try { return ResolvePath(path); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path is required.", nameof(path));
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        if (path.Split(separators, StringSplitOptions.RemoveEmptyEntries).Contains(".."))
            throw new UnauthorizedAccessException($"Path '{path}' contains a parent-directory segment.");

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(next); }
            catch (FileNotFoundException) { current = Path.GetFullPath(next); continue; }
            catch (DirectoryNotFoundException) { current = Path.GetFullPath(next); continue; }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = Path.GetFullPath(next);
                continue;
            }

            var link = Directory.Exists(next)
                ? Directory.ResolveLinkTarget(next, returnFinalTarget: true)
                : File.ResolveLinkTarget(next, returnFinalTarget: true);
            if (link is null)
                throw new UnauthorizedAccessException($"Could not resolve link '{next}'.");
            current = Path.GetFullPath(link.FullName);
        }
        return current;
    }

    private async Task<T> WithDatabaseAsync<T>(
        Func<AuthDbContext, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await action(database, cancellationToken);
    }
}
