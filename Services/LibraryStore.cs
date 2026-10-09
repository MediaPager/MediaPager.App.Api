using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Services;

/// <summary>Generic plugin access to library items. It is intentionally independent of any
/// plugin capability; plugins own the meaning and lifecycle of the files they create.</summary>
public sealed class LibraryStore(IServiceScopeFactory scopeFactory) : ILibraryStore
{
    public async Task<LibraryCatalogInfo?> GetCatalogAsync(int catalogId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await database.Catalogs.AsNoTracking()
            .Where(catalog => catalog.Id == catalogId)
            .Select(catalog => new LibraryCatalogInfo(
                catalog.Id,
                catalog.CatalogTypeId,
                catalog.CatalogType!.Slug,
                catalog.Name,
                catalog.Path))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<LibraryItemHandle> SaveAsync(LibraryItemWriteRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        if (request.CatalogItemId is int existingId)
        {
            var exists = await database.CatalogItems.AnyAsync(item => item.Id == existingId, cancellationToken);
            return new LibraryItemHandle(exists ? existingId : null, Created: false);
        }

        Catalog? catalog;
        if (request.CatalogId is int selectedCatalogId)
        {
            catalog = await database.Catalogs
                .Include(entry => entry.CatalogType!)
                .ThenInclude(type => type!.MediaType)
                .FirstOrDefaultAsync(entry => entry.Id == selectedCatalogId, cancellationToken);
        }
        else
        {
            var catalogTypeId = await ResolveCatalogTypeIdAsync(database, request, cancellationToken);
            if (catalogTypeId is null) return new LibraryItemHandle(null, Created: false);
            catalog = await database.Catalogs
                .Include(entry => entry.CatalogType!)
                .ThenInclude(type => type!.MediaType)
                .Where(entry => entry.CatalogTypeId == catalogTypeId.Value)
                .OrderBy(entry => entry.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        if (catalog?.CatalogType?.MediaType is null) return new LibraryItemHandle(null, Created: false);

        if (!string.IsNullOrWhiteSpace(request.ExternalId))
        {
            var existing = await database.CatalogItems.AsNoTracking()
                .Where(item => item.CatalogId == catalog.Id && item.ExternalId == request.ExternalId)
                .Select(item => (int?)item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is int itemId) return new LibraryItemHandle(itemId, Created: false);
        }

        var item = new CatalogItem
        {
            CatalogId = catalog.Id,
            MediaTypeId = catalog.CatalogType.MediaType.Id,
            Title = request.Title,
            Kind = request.Kind switch
            {
                MediaKind.Movie => "movie",
                MediaKind.Tv => "show",
                MediaKind.Music => "track",
                MediaKind.Podcast => "podcast",
                MediaKind.Audiobook => "audiobook",
                MediaKind.Book => "book",
                _ => catalog.CatalogType.Slug,
            },
            ExternalId = request.ExternalId,
            ImageUrl = request.ImageUrl,
            BackdropUrl = request.BackdropUrl,
            Overview = request.Overview,
            Year = request.Year,
        };
        database.CatalogItems.Add(item);
        await database.SaveChangesAsync(cancellationToken);
        return new LibraryItemHandle(item.Id, Created: true);
    }

    public async Task UpdateStoragePathAsync(int itemId, string path, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == itemId, cancellationToken);
        if (item is null) return;
        item.StoragePath = path;
        item.SizeBytes = File.Exists(path) ? new FileInfo(path).Length : null;
        item.ModifiedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int itemId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == itemId, cancellationToken);
        if (item is null) return;
        database.CatalogItems.Remove(item);
        await database.SaveChangesAsync(cancellationToken);
    }

    private static async Task<int?> ResolveCatalogTypeIdAsync(
        AuthDbContext database,
        LibraryItemWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CatalogTypeId is int typeId &&
            await database.CatalogTypes.AnyAsync(type => type.Id == typeId, cancellationToken))
            return typeId;

        var slug = request.Kind switch
        {
            MediaKind.Movie => "movies",
            MediaKind.Tv => "tv-shows",
            MediaKind.Music => "music",
            MediaKind.Podcast => "podcasts",
            MediaKind.Audiobook => "audiobooks",
            MediaKind.Book => "books",
            _ => null,
        };
        if (slug is null) return null;
        return await database.CatalogTypes.AsNoTracking()
            .Where(type => type.Slug == slug)
            .Select(type => (int?)type.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
