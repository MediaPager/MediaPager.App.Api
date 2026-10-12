using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Services;

/// <summary>Generic plugin access to library items. It is intentionally independent of any
/// plugin capability; plugins own the meaning and lifecycle of the files they create.</summary>
public sealed class LibraryStore(IServiceScopeFactory scopeFactory) : ILibraryStore
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> MusicCatalogLocks = new();
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> TvCatalogLocks = new();

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

        if (request.Kind == MediaKind.Music && request.MusicMetadata is { } musicMetadata)
        {
            var gate = MusicCatalogLocks.GetOrAdd(catalog.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await SaveMusicTrackAsync(database, catalog, request, musicMetadata, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        if (request.Kind == MediaKind.Tv &&
            !string.IsNullOrWhiteSpace(request.SeriesExternalId) &&
            request.SeasonNumber is > 0 && request.EpisodeNumber is > 0)
        {
            var gate = TvCatalogLocks.GetOrAdd(catalog.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await SaveTvEpisodeAsync(database, catalog, request, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

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

    private static async Task<LibraryItemHandle> SaveTvEpisodeAsync(
        AuthDbContext database,
        Catalog catalog,
        LibraryItemWriteRequest request,
        CancellationToken cancellationToken)
    {
        var episodeExternalId = string.IsNullOrWhiteSpace(request.ExternalId) ? null : request.ExternalId.Trim();
        if (episodeExternalId is not null)
        {
            var existingEpisodeId = await database.CatalogItems.AsNoTracking()
                .Where(item => item.CatalogId == catalog.Id && item.Kind == "episode" && item.ExternalId == episodeExternalId)
                .Select(item => (int?)item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existingEpisodeId is int existingId)
                return new LibraryItemHandle(existingId, Created: false);
        }

        var seriesExternalId = request.SeriesExternalId!.Trim();
        var series = await database.CatalogItems.FirstOrDefaultAsync(item =>
            item.CatalogId == catalog.Id && item.Kind == "show" && item.ExternalId == seriesExternalId,
            cancellationToken);
        if (series is null)
        {
            series = new CatalogItem
            {
                CatalogId = catalog.Id,
                MediaTypeId = catalog.CatalogType!.MediaType!.Id,
                Title = string.IsNullOrWhiteSpace(request.SeriesTitle) ? request.Title : request.SeriesTitle.Trim(),
                Kind = "show",
                ExternalId = seriesExternalId,
                ImageUrl = request.SeriesImageUrl,
                BackdropUrl = request.SeriesBackdropUrl,
                Overview = request.SeriesOverview,
                Year = request.SeriesYear,
            };
            database.CatalogItems.Add(series);
            await database.SaveChangesAsync(cancellationToken);
        }

        var seasonNumber = request.SeasonNumber!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var season = await database.CatalogItems.FirstOrDefaultAsync(item =>
            item.CatalogId == catalog.Id && item.Kind == "season" && item.ParentId == series.Id &&
            item.SeasonNumber == seasonNumber,
            cancellationToken);
        if (season is null)
        {
            season = new CatalogItem
            {
                CatalogId = catalog.Id,
                MediaTypeId = catalog.CatalogType!.MediaType!.Id,
                ParentId = series.Id,
                Title = string.IsNullOrWhiteSpace(request.SeasonTitle) ? $"Season {seasonNumber}" : request.SeasonTitle.Trim(),
                Kind = "season",
                SeasonNumber = seasonNumber,
            };
            database.CatalogItems.Add(season);
            await database.SaveChangesAsync(cancellationToken);
        }

        var episode = new CatalogItem
        {
            CatalogId = catalog.Id,
            MediaTypeId = catalog.CatalogType!.MediaType!.Id,
            ParentId = season.Id,
            Title = request.Title,
            Kind = "episode",
            ExternalId = episodeExternalId,
            SeasonNumber = seasonNumber,
            EpisodeNumber = request.EpisodeNumber.GetValueOrDefault().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ImageUrl = request.ImageUrl,
            BackdropUrl = request.BackdropUrl ?? request.SeriesBackdropUrl,
            Overview = request.Overview,
            Year = request.Year,
        };
        database.CatalogItems.Add(episode);
        await database.SaveChangesAsync(cancellationToken);
        return new LibraryItemHandle(episode.Id, Created: true);
    }

    private static async Task<LibraryItemHandle> SaveMusicTrackAsync(
        AuthDbContext database,
        Catalog catalog,
        LibraryItemWriteRequest request,
        MusicTrackMetadata metadata,
        CancellationToken cancellationToken)
    {
        var externalId = string.IsNullOrWhiteSpace(request.ExternalId) ? null : request.ExternalId.Trim();
        if (externalId is not null)
        {
            var existing = await database.CatalogItems.AsNoTracking()
                .Where(item => item.CatalogId == catalog.Id && item.Kind == "track" && item.ExternalId == externalId)
                .Select(item => (int?)item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is int existingId) return new LibraryItemHandle(existingId, Created: false);
        }

        var albumArtists = NormalizeCredits(metadata.AlbumArtists);
        var trackArtists = NormalizeCredits(metadata.Artists);
        if (albumArtists.Count == 0)
            albumArtists = trackArtists.Count > 0
                ? [trackArtists[0]]
                : [new MusicArtistCredit("Unknown Artist")];

        var artistItems = new List<CatalogItem>(albumArtists.Count);
        foreach (var credit in albumArtists)
            artistItems.Add(await FindOrCreateMusicArtistAsync(database, catalog, credit, cancellationToken));
        var primaryArtist = artistItems[0];

        var albumTitle = string.IsNullOrWhiteSpace(metadata.Album) ? "Singles" : metadata.Album.Trim();
        var album = await FindMusicAlbumAsync(database, catalog.Id, primaryArtist.Id, metadata.MusicBrainzReleaseId,
            albumTitle, cancellationToken);
        if (album is null)
        {
            album = new CatalogItem
            {
                CatalogId = catalog.Id,
                MediaTypeId = catalog.CatalogType!.MediaType!.Id,
                ParentId = primaryArtist.Id,
                Title = albumTitle,
                SortTitle = albumTitle,
                Kind = "album",
                Year = metadata.Year ?? request.Year,
                ImageUrl = request.ImageUrl,
                Overview = request.Overview,
                MusicBrainzId = metadata.MusicBrainzReleaseId,
            };
            database.CatalogItems.Add(album);
            await database.SaveChangesAsync(cancellationToken);
        }
        await ReplaceMusicArtistCreditsAsync(database, catalog, album, "album-artist", albumArtists, cancellationToken);
        await AddMusicGenresAsync(database, album, metadata.Genres, cancellationToken);

        var recordingId = string.IsNullOrWhiteSpace(metadata.MusicBrainzRecordingId)
            ? null
            : metadata.MusicBrainzRecordingId.Trim();
        if (recordingId is not null)
        {
            var existing = await database.CatalogItems.AsNoTracking()
                .Where(item => item.CatalogId == catalog.Id && item.Kind == "track" &&
                               item.ParentId == album.Id && item.MusicBrainzId == recordingId)
                .Select(item => (int?)item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is int existingId) return new LibraryItemHandle(existingId, Created: false);
        }

        var track = new CatalogItem
        {
            CatalogId = catalog.Id,
            MediaTypeId = catalog.CatalogType!.MediaType!.Id,
            ParentId = album.Id,
            Title = request.Title,
            SortTitle = metadata.TrackNumber is int trackNumber
                ? $"{metadata.DiscNumber.GetValueOrDefault(1):D2}-{trackNumber:D3} {request.Title}"
                : request.Title,
            Kind = "track",
            ExternalId = externalId,
            MusicBrainzId = recordingId,
            Isrc = string.IsNullOrWhiteSpace(metadata.Isrc) ? null : metadata.Isrc.Trim(),
            TrackNumber = metadata.TrackNumber,
            TrackCount = metadata.TrackCount,
            DiscNumber = metadata.DiscNumber,
            DiscCount = metadata.DiscCount,
            Year = metadata.Year ?? request.Year,
            ImageUrl = request.ImageUrl,
            BackdropUrl = request.BackdropUrl,
            Overview = request.Overview,
        };
        database.CatalogItems.Add(track);
        await database.SaveChangesAsync(cancellationToken);

        if (trackArtists.Count == 0) trackArtists = albumArtists;
        await ReplaceMusicArtistCreditsAsync(database, catalog, track, "artist", trackArtists, cancellationToken);
        await AddMusicGenresAsync(database, track, metadata.Genres, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return new LibraryItemHandle(track.Id, Created: true);
    }

    private static IReadOnlyList<MusicArtistCredit> NormalizeCredits(IReadOnlyList<MusicArtistCredit>? credits) =>
        (credits ?? [])
        .Select(credit => new MusicArtistCredit(credit.Name.Trim(),
            string.IsNullOrWhiteSpace(credit.MusicBrainzId) ? null : credit.MusicBrainzId.Trim()))
        .Where(credit => credit.Name.Length > 0)
        .DistinctBy(credit => credit.MusicBrainzId ?? credit.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static async Task<CatalogItem> FindOrCreateMusicArtistAsync(
        AuthDbContext database,
        Catalog catalog,
        MusicArtistCredit credit,
        CancellationToken cancellationToken)
    {
        var query = database.CatalogItems.Where(item =>
            item.CatalogId == catalog.Id && item.Kind == "artist" && item.ParentId == null);
        var artist = credit.MusicBrainzId is not null
            ? await query.FirstOrDefaultAsync(item => item.MusicBrainzId == credit.MusicBrainzId, cancellationToken)
            : await query.FirstOrDefaultAsync(item => item.Title.ToLower() == credit.Name.ToLower(), cancellationToken);
        if (artist is not null) return artist;

        artist = new CatalogItem
        {
            CatalogId = catalog.Id,
            MediaTypeId = catalog.CatalogType!.MediaType!.Id,
            Title = credit.Name,
            SortTitle = credit.Name,
            Kind = "artist",
            MusicBrainzId = credit.MusicBrainzId,
        };
        database.CatalogItems.Add(artist);
        await database.SaveChangesAsync(cancellationToken);
        return artist;
    }

    private static Task<CatalogItem?> FindMusicAlbumAsync(
        AuthDbContext database,
        int catalogId,
        int primaryArtistId,
        string? musicBrainzReleaseId,
        string title,
        CancellationToken cancellationToken)
    {
        var query = database.CatalogItems.Where(item => item.CatalogId == catalogId && item.Kind == "album");
        return !string.IsNullOrWhiteSpace(musicBrainzReleaseId)
            ? query.FirstOrDefaultAsync(item => item.MusicBrainzId == musicBrainzReleaseId, cancellationToken)
            : query.FirstOrDefaultAsync(item => item.ParentId == primaryArtistId &&
                                                item.Title.ToLower() == title.ToLower(), cancellationToken);
    }

    private static async Task ReplaceMusicArtistCreditsAsync(
        AuthDbContext database,
        Catalog catalog,
        CatalogItem item,
        string role,
        IReadOnlyList<MusicArtistCredit> credits,
        CancellationToken cancellationToken)
    {
        var existing = await database.CatalogItemMusicArtistCredits
            .Where(credit => credit.CatalogItemId == item.Id && credit.Role == role)
            .ToListAsync(cancellationToken);
        database.CatalogItemMusicArtistCredits.RemoveRange(existing);
        var position = 0;
        foreach (var credit in credits)
        {
            var artist = await FindOrCreateMusicArtistAsync(database, catalog, credit, cancellationToken);
            database.CatalogItemMusicArtistCredits.Add(new CatalogItemMusicArtistCredit
            {
                CatalogItemId = item.Id,
                ArtistCatalogItemId = artist.Id,
                Role = role,
                Position = position++,
            });
        }
    }

    private static async Task AddMusicGenresAsync(
        AuthDbContext database,
        CatalogItem item,
        IReadOnlyList<string>? genres,
        CancellationToken cancellationToken)
    {
        foreach (var genre in (genres ?? []).Select(value => value.Trim()).Where(value => value.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exists = await database.CatalogItemTags.AnyAsync(tag =>
                tag.CatalogItemId == item.Id && tag.Type == "genre" && tag.Value == genre, cancellationToken);
            if (!exists)
                database.CatalogItemTags.Add(new CatalogItemTag { CatalogItemId = item.Id, Type = "genre", Value = genre });
        }
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
        var previousParentId = item.ParentId;
        var creditedArtistIds = await database.CatalogItemMusicArtistCredits.AsNoTracking()
            .Where(credit => credit.CatalogItemId == item.Id)
            .Select(credit => credit.ArtistCatalogItemId)
            .ToListAsync(cancellationToken);
        database.CatalogItems.Remove(item);
        await database.SaveChangesAsync(cancellationToken);
        if (previousParentId is int parentId) creditedArtistIds.Add(parentId);
        await MusicCatalogHierarchy.PruneEmptyAncestorsAsync(database, creditedArtistIds, cancellationToken);
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
