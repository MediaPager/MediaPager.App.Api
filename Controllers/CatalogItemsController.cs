using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediaPager.App.Api.Services;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("catalog-items")]
public sealed class CatalogItemsController(
    AuthDbContext database,
    ArtworkStorage artwork,
    StorageLayout storageLayout,
    LocalFileSessionState localSessions,
    IRuntimeSettings settings,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    private const long MaxMediaUploadBytes = 50L * 1024 * 1024 * 1024;
    private static readonly string[] TagTypes = ["genre", "country", "writer", "director", "producer"];

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".webm", ".ts" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".oga", ".wav", ".opus", ".wma" };
    private static readonly HashSet<string> BookExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".epub", ".mobi", ".azw", ".azw3", ".cbz", ".cbr", ".txt", ".rtf" };
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".csv", ".xls", ".xlsx", ".ppt", ".pptx", ".odp", ".ods" };

    // GET /catalog-items — library entries for the catalog tabs. Filter by catalog,
    // kind, or external id (playback uses externalId to find a local copy of a TMDB movie).
    [HttpGet("music/stats")]
    public async Task<IActionResult> MusicStats([FromQuery] int catalogId, CancellationToken cancellationToken)
    {
        if (catalogId <= 0) return BadRequest(new { error = "A music catalog is required." });
        var isMusicCatalog = await database.Catalogs.AsNoTracking()
            .AnyAsync(catalog => catalog.Id == catalogId && catalog.CatalogType!.Slug == "music", cancellationToken);
        if (!isMusicCatalog) return NotFound(new { error = "The music catalog was not found." });

        var musicItems = database.CatalogItems.AsNoTracking().Where(item => item.CatalogId == catalogId);
        var artists = await musicItems.CountAsync(artist => artist.Kind == "artist" && artist.ParentId == null &&
            (database.CatalogItems.Any(album => album.CatalogId == catalogId && album.Kind == "album" && album.ParentId == artist.Id &&
                database.CatalogItems.Any(track => track.CatalogId == catalogId && track.ParentId == album.Id && track.Kind != "artist" && track.Kind != "album")) ||
             database.CatalogItemMusicArtistCredits.Any(credit => credit.ArtistCatalogItemId == artist.Id &&
                credit.CatalogItem!.CatalogId == catalogId && credit.CatalogItem.Kind != "artist" && credit.CatalogItem.Kind != "album")),
            cancellationToken);
        var albums = await musicItems.CountAsync(album => album.Kind == "album" &&
            database.CatalogItems.Any(track => track.CatalogId == catalogId && track.ParentId == album.Id &&
                                               track.Kind != "artist" && track.Kind != "album"), cancellationToken);
        var tracks = await musicItems.CountAsync(item => item.Kind != "artist" && item.Kind != "album", cancellationToken);
        var unorganizedTracks = await musicItems.CountAsync(item => item.Kind != "artist" && item.Kind != "album" && item.ParentId == null, cancellationToken);
        var genres = await database.CatalogItemTags.AsNoTracking()
            .Where(tag => tag.Type == "genre" && tag.CatalogItem!.CatalogId == catalogId &&
                          tag.CatalogItem.Kind != "artist" && tag.CatalogItem.Kind != "album")
            .GroupBy(tag => tag.Value)
            .Select(group => new { name = group.Key, count = group.Count() })
            .OrderByDescending(genre => genre.count)
            .ThenBy(genre => genre.name)
            .ToListAsync(cancellationToken);
        var years = await musicItems.Where(item => item.Kind != "artist" && item.Kind != "album" && item.Year != null)
            .Select(item => item.Year!.Value)
            .Distinct()
            .OrderByDescending(value => value)
            .ToListAsync(cancellationToken);

        return Ok(new { artists, albums, tracks, unorganizedTracks, genres, years });
    }

    [HttpGet("music")]
    public async Task<IActionResult> MusicList(
        [FromQuery] int catalogId,
        [FromQuery] string level = "artist",
        [FromQuery] int? parentId = null,
        [FromQuery] string? queryText = null,
        [FromQuery] string? genre = null,
        [FromQuery] int? year = null,
        [FromQuery] string sort = "title",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 60,
        CancellationToken cancellationToken = default)
    {
        if (catalogId <= 0) return BadRequest(new { error = "A music catalog is required." });
        level = level.Trim().ToLowerInvariant();
        if (level is not ("artist" or "album" or "track" or "unorganized"))
            return BadRequest(new { error = "Level must be artist, album, track or unorganized." });
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var isMusicCatalog = await database.Catalogs.AsNoTracking()
            .AnyAsync(catalog => catalog.Id == catalogId && catalog.CatalogType!.Slug == "music", cancellationToken);
        if (!isMusicCatalog) return NotFound(new { error = "The music catalog was not found." });

        var query = database.CatalogItems.AsNoTracking()
            .Include(item => item.Parent)
            .Include(item => item.Catalog!)
            .ThenInclude(catalog => catalog!.CatalogType!)
            .ThenInclude(type => type!.MediaType!)
            .Where(item => item.CatalogId == catalogId);
        if (level == "unorganized")
            query = query.Where(item => item.Kind != "artist" && item.Kind != "album" && item.ParentId == null);
        else if (level == "track")
            query = query.Where(item => item.Kind != "artist" && item.Kind != "album");
        else
            query = query.Where(item => item.Kind == level);
        if (level == "artist")
        {
            query = query.Where(artist => artist.ParentId == null &&
                (database.CatalogItems.Any(album => album.CatalogId == catalogId && album.Kind == "album" && album.ParentId == artist.Id &&
                    database.CatalogItems.Any(track => track.CatalogId == catalogId && track.ParentId == album.Id && track.Kind != "artist" && track.Kind != "album")) ||
                 database.CatalogItemMusicArtistCredits.Any(credit => credit.ArtistCatalogItemId == artist.Id &&
                    credit.CatalogItem!.CatalogId == catalogId && credit.CatalogItem.Kind != "artist" && credit.CatalogItem.Kind != "album")));
        }
        else if (level == "album")
        {
            query = query.Where(album => database.CatalogItems.Any(track => track.CatalogId == catalogId &&
                track.ParentId == album.Id && track.Kind != "artist" && track.Kind != "album"));
        }
        if ((level is "album" or "track") && parentId is int parent)
            query = query.Where(item => item.ParentId == parent);
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            var normalizedQuery = queryText.Trim().ToLowerInvariant();
            query = query.Where(item => item.Title.ToLower().Contains(normalizedQuery) ||
                                        (item.Parent != null && item.Parent.Title.ToLower().Contains(normalizedQuery)) ||
                                        (item.Parent != null && item.Parent.Parent != null && item.Parent.Parent.Title.ToLower().Contains(normalizedQuery)) ||
                                        item.MusicArtistCredits.Any(credit => credit.ArtistCatalogItem!.Title.ToLower().Contains(normalizedQuery)));
        }
        if (!string.IsNullOrWhiteSpace(genre))
        {
            var normalizedGenre = genre.Trim().ToLowerInvariant();
            query = query.Where(item => item.Tags.Any(tag => tag.Type == "genre" && tag.Value.ToLower() == normalizedGenre));
        }
        if (year is int selectedYear) query = query.Where(item => item.Year == selectedYear);

        var artistCreditRole = level == "album" ? "album-artist" : "artist";
        var totalItems = await query.CountAsync(cancellationToken);
        query = sort.Trim().ToLowerInvariant() switch
        {
            "artist" => query.OrderBy(item => item.MusicArtistCredits
                .Where(credit => credit.Role == artistCreditRole)
                .OrderBy(credit => credit.Position)
                .Select(credit => credit.ArtistCatalogItem!.Title)
                .FirstOrDefault() ?? "")
                .ThenBy(item => item.SortTitle ?? item.Title),
            "album" => query.OrderBy(item => item.Parent!.Title).ThenBy(item => item.SortTitle ?? item.Title),
            "year" => query.OrderBy(item => item.Year == null).ThenByDescending(item => item.Year).ThenBy(item => item.SortTitle ?? item.Title),
            // SQLite stores DateTimeOffset values as text but cannot order by them through
            // EF Core; monotonically increasing item ids provide a portable recent-first key.
            "recent" => query.OrderByDescending(item => item.Id),
            _ => query.OrderBy(item => item.SortTitle ?? item.Title).ThenBy(item => item.Id),
        };
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            page,
            pageSize,
            totalItems,
            totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize)),
            items = await ToDtosAsync(items, cancellationToken),
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? catalogId,
        [FromQuery] string? kind,
        [FromQuery] string? externalId,
        CancellationToken cancellationToken)
    {
        var query = database.CatalogItems.AsNoTracking()
            .Include(item => item.Parent)
            .Include(item => item.Catalog!)
            .ThenInclude(catalog => catalog!.CatalogType!)
            .ThenInclude(type => type!.MediaType!)
            .OrderBy(item => item.SortTitle ?? item.Title)
            .AsQueryable();
        if (catalogId is int catalog) query = query.Where(item => item.CatalogId == catalog);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(item => item.Kind == kind.Trim());
        if (!string.IsNullOrWhiteSpace(externalId)) query = query.Where(item => item.ExternalId == externalId.Trim());
        var items = await query.ToListAsync(cancellationToken);
        return Ok(await ToDtosAsync(items, cancellationToken));
    }

    [HttpPost("upload")]
    [Authorize(Policy = "CanEditCatalogs")]
    [RequestSizeLimit(MaxMediaUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxMediaUploadBytes)]
    public async Task<IActionResult> Upload(
        [FromForm] UploadCatalogItemRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CatalogId <= 0) return BadRequest(new { error = "Choose a catalog." });
        var file = request.File;
        if (file is not { Length: > 0 }) return BadRequest(new { error = "Choose a media file." });
        if (file.Length > MaxMediaUploadBytes) return BadRequest(new { error = "The upload exceeds the 50 GB limit." });

        var catalog = await database.Catalogs
            .Include(entry => entry.CatalogType!)
            .ThenInclude(type => type!.MediaType)
            .FirstOrDefaultAsync(entry => entry.Id == request.CatalogId, cancellationToken);
        if (catalog?.CatalogType?.MediaType is null) return NotFound(new { error = "The catalog was not found." });
        if (string.IsNullOrWhiteSpace(catalog.Path))
            return BadRequest(new { error = "Set a folder path for this catalog before uploading media." });

        string? kind;
        HashSet<string>? allowedExtensions;
        switch (catalog.CatalogType.Slug.ToLowerInvariant())
        {
            case "movies": (kind, allowedExtensions) = ("movie", VideoExtensions); break;
            case "tv-shows": (kind, allowedExtensions) = ("show", VideoExtensions); break;
            case "music": (kind, allowedExtensions) = ("track", AudioExtensions); break;
            case "podcasts": (kind, allowedExtensions) = ("podcast", AudioExtensions); break;
            case "audiobooks": (kind, allowedExtensions) = ("audiobook", AudioExtensions); break;
            case "books": (kind, allowedExtensions) = ("book", BookExtensions); break;
            case "docs": (kind, allowedExtensions) = ("document", DocumentExtensions); break;
            default: (kind, allowedExtensions) = (null, null); break;
        }
        if (kind is null || allowedExtensions is null)
            return BadRequest(new { error = "This catalog type does not accept media uploads." });

        var originalName = Path.GetFileName(file.FileName.Replace('\\', '/'));
        var extension = Path.GetExtension(originalName);
        if (!allowedExtensions.Contains(extension))
            return BadRequest(new { error = $"Files with the {extension} extension cannot be added to this catalog." });

        if (request.Year is < 1800 or > 2200) return BadRequest(new { error = "Year must be between 1800 and 2200." });
        if (request.Rating is < 0 or > 10) return BadRequest(new { error = "Rating must be between 0 and 10." });

        DateTime? originalAvailableAt = null;
        if (!string.IsNullOrWhiteSpace(request.OriginalAvailableAt))
        {
            if (!DateTime.TryParse(request.OriginalAvailableAt.Trim(), out var parsedDate))
                return BadRequest(new { error = "Original available date must be a valid date." });
            originalAvailableAt = parsedDate;
        }

        var safeName = SafeUploadFileName(originalName, extension);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(catalog.Path));
        Directory.CreateDirectory(root);
        var storagePath = UniqueUploadPath(root, safeName);
        var relativePath = Path.GetRelativePath(root, storagePath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            return BadRequest(new { error = "The upload path is invalid." });

        if (!string.IsNullOrWhiteSpace(request.ExternalId) &&
            await database.CatalogItems.AnyAsync(item =>
                item.CatalogId == catalog.Id && item.ExternalId == request.ExternalId.Trim(), cancellationToken))
        {
            return Conflict(new { error = "This title is already in the selected catalog." });
        }

        var item = new CatalogItem
        {
            CatalogId = catalog.Id,
            MediaTypeId = catalog.CatalogType.MediaType.Id,
            Title = string.IsNullOrWhiteSpace(request.Title)
                ? Path.GetFileNameWithoutExtension(safeName)
                : request.Title.Trim(),
            Kind = kind,
            StoragePath = storagePath,
            SizeBytes = file.Length,
            Year = request.Year,
            Overview = string.IsNullOrWhiteSpace(request.Overview) ? null : request.Overview.Trim(),
            ExternalId = string.IsNullOrWhiteSpace(request.ExternalId) ? null : request.ExternalId.Trim(),
            ImageUrl = NormalizeUrl(request.ImageUrl ?? ""),
            BackdropUrl = NormalizeUrl(request.BackdropUrl ?? ""),
            OriginalTitle = string.IsNullOrWhiteSpace(request.OriginalTitle) ? null : request.OriginalTitle.Trim(),
            OriginalAvailableAt = originalAvailableAt,
            ContentRating = string.IsNullOrWhiteSpace(request.ContentRating) ? null : request.ContentRating.Trim().ToUpperInvariant(),
            Rating = request.Rating,
            ImdbId = CleanIdentifier(request.ImdbId),
            Isbn = CleanIdentifier(request.Isbn),
            MusicBrainzId = CleanIdentifier(request.MusicBrainzId),
            Isrc = CleanIdentifier(request.Isrc),
        };
        foreach (var genre in (request.Genres ?? []).Select(value => value.Trim()).Where(value => value.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            item.Tags.Add(new CatalogItemTag { Type = "genre", Value = genre });
        }

        try
        {
            await using (var output = new FileStream(storagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 1024 * 1024, useAsync: true))
            {
                await file.CopyToAsync(output, cancellationToken);
            }
            database.CatalogItems.Add(item);
            await database.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            TryDelete(storagePath);
            throw;
        }

        var created = await LoadAsync(item.Id, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, await ToDtoAsync(created!, cancellationToken));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var item = await LoadAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(await ToDtoAsync(item, cancellationToken));
    }

    // PUT /catalog-items/{id} — edit a library entry's display metadata: rename it,
    // swap the poster/backdrop (URL or uploaded file), fix the year, rewrite the
    // description, set sort title / original title / release date / content rating /
    // official rating. Only null fields are left alone; the physical file never changes.
    [HttpPut("{id:int}")]
    [Authorize(Policy = "CanEditCatalogs")]
    public async Task<IActionResult> Update(int id, UpdateCatalogItemRequest request, CancellationToken cancellationToken)
    {
        var item = await database.CatalogItems
            .Include(entry => entry.Parent)
            .Include(entry => entry.Catalog!)
            .ThenInclude(catalog => catalog!.CatalogType)
            .FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();
        var originalParentId = item.ParentId;
        var previousArtistIds = request.Artist is null
            ? []
            : await database.CatalogItemMusicArtistCredits.AsNoTracking()
                .Where(credit => credit.CatalogItemId == item.Id && credit.Role == "artist")
                .Select(credit => credit.ArtistCatalogItemId)
                .ToListAsync(cancellationToken);

        if (request.Title is not null)
        {
            var title = request.Title.Trim();
            if (title.Length == 0) return BadRequest(new { error = "A title is required." });
            item.Title = title;
        }
        if (request.Overview is not null)
            item.Overview = string.IsNullOrWhiteSpace(request.Overview) ? null : request.Overview.Trim();
        if (request.ImageUrl is not null) item.ImageUrl = NormalizeUrl(request.ImageUrl);
        if (request.BackdropUrl is not null) item.BackdropUrl = NormalizeUrl(request.BackdropUrl);
        if (request.SortTitle is not null)
            item.SortTitle = string.IsNullOrWhiteSpace(request.SortTitle) ? null : request.SortTitle.Trim();
        if (request.OriginalTitle is not null)
            item.OriginalTitle = string.IsNullOrWhiteSpace(request.OriginalTitle) ? null : request.OriginalTitle.Trim();
        if (request.ExternalId is not null) item.ExternalId = CleanIdentifier(request.ExternalId);
        if (request.ImdbId is not null) item.ImdbId = CleanIdentifier(request.ImdbId);
        if (request.Isbn is not null) item.Isbn = CleanIdentifier(request.Isbn);
        if (request.MusicBrainzId is not null) item.MusicBrainzId = CleanIdentifier(request.MusicBrainzId);
        if (request.Isrc is not null) item.Isrc = CleanIdentifier(request.Isrc);
        if (request.ContentRating is not null)
            item.ContentRating = string.IsNullOrWhiteSpace(request.ContentRating) ? null : request.ContentRating.Trim().ToUpperInvariant();
        if (request.OriginalAvailableAt is not null)
        {
            if (string.IsNullOrWhiteSpace(request.OriginalAvailableAt))
                item.OriginalAvailableAt = null;
            else if (!DateTime.TryParse(request.OriginalAvailableAt.Trim(), out var availableAt))
                return BadRequest(new { error = "Original available date must be a valid date." });
            else
                item.OriginalAvailableAt = availableAt;
        }
        if (request.Rating is not null)
        {
            if (request.Rating is < 0 or > 10) return BadRequest(new { error = "Rating must be between 0 and 10." });
            item.Rating = request.Rating;
        }
        if (request.Year is not null)
        {
            if (request.Year is < 1800 or > 2200) return BadRequest(new { error = "Year must be between 1800 and 2200." });
            item.Year = request.Year;
        }
        if (request.Artist is not null || request.Album is not null)
        {
            if (!string.Equals(item.Catalog?.CatalogType?.Slug, "music", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Artist and album fields are only available for music catalog items." });
            if (item.Kind is "artist" or "album")
                return BadRequest(new { error = "Edit artist and album entries from a track's music metadata." });
            await UpdateMusicOrganizationAsync(item, request.Artist, request.Album, cancellationToken);
        }
        if (request.Year is not null && item.Parent is { Kind: "album" } release)
            release.Year = request.Year;
        item.ModifiedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        if (originalParentId != item.ParentId)
            previousArtistIds.Add(originalParentId ?? 0);
        await MusicCatalogHierarchy.PruneEmptyAncestorsAsync(
            database, previousArtistIds.Where(candidateId => candidateId > 0), cancellationToken);

        var updated = await LoadAsync(id, cancellationToken);
        return Ok(await ToDtoAsync(updated!, cancellationToken));
    }

    private async Task UpdateMusicOrganizationAsync(
        CatalogItem track,
        string? artistValue,
        string? albumValue,
        CancellationToken cancellationToken)
    {
        var artistNames = artistValue is null
            ? await MusicCreditNamesAsync(track.Id, "artist", cancellationToken)
            : ParseMusicNames(artistValue);
        if (artistValue is not null)
            await ReplaceMusicCreditsAsync(track, "artist", artistNames, cancellationToken);

        var currentAlbum = track.Parent is { Kind: "album" } ? track.Parent : null;
        var albumTitle = albumValue is null
            ? currentAlbum?.Title
            : string.IsNullOrWhiteSpace(albumValue) ? null : albumValue.Trim();
        if (string.IsNullOrWhiteSpace(albumTitle))
        {
            track.Kind = "track";
            track.Parent = null;
            track.ParentId = null;
            return;
        }

        var albumArtistNames = currentAlbum is null
            ? []
            : await MusicCreditNamesAsync(currentAlbum.Id, "album-artist", cancellationToken);
        var primaryAlbumArtistName = albumArtistNames.FirstOrDefault()
            ?? artistNames.FirstOrDefault()
            ?? "Unknown Artist";
        var catalogId = track.CatalogId;
        var artist = await FindOrCreateMusicArtistAsync(track, primaryAlbumArtistName, cancellationToken);
        var album = await database.CatalogItems.FirstOrDefaultAsync(item =>
            item.CatalogId == catalogId && item.Kind == "album" && item.ParentId == artist.Id &&
            item.Title.ToLower() == albumTitle.ToLower(), cancellationToken);
        if (album is null)
        {
            album = new CatalogItem
            {
                CatalogId = catalogId,
                MediaTypeId = track.MediaTypeId,
                ParentId = artist.Id,
                Title = albumTitle,
                SortTitle = albumTitle,
                Kind = "album",
                Year = track.Year,
                ImageUrl = track.ImageUrl,
            };
            database.CatalogItems.Add(album);
            await database.SaveChangesAsync(cancellationToken);
            await ReplaceMusicCreditsAsync(album, "album-artist", [primaryAlbumArtistName], cancellationToken);
        }

        track.Kind = "track";
        track.Parent = album;
        track.ParentId = album.Id;
    }

    private async Task<CatalogItem> FindOrCreateMusicArtistAsync(
        CatalogItem item,
        string name,
        CancellationToken cancellationToken)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Unknown Artist" : name.Trim();
        var artist = await database.CatalogItems.FirstOrDefaultAsync(candidate =>
            candidate.CatalogId == item.CatalogId && candidate.Kind == "artist" && candidate.ParentId == null &&
            candidate.Title.ToLower() == name.ToLower(), cancellationToken);
        if (artist is not null) return artist;

        artist = new CatalogItem
        {
            CatalogId = item.CatalogId,
            MediaTypeId = item.MediaTypeId,
            Title = name,
            SortTitle = name,
            Kind = "artist",
        };
        database.CatalogItems.Add(artist);
        await database.SaveChangesAsync(cancellationToken);
        return artist;
    }

    private async Task ReplaceMusicCreditsAsync(
        CatalogItem item,
        string role,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var existing = await database.CatalogItemMusicArtistCredits
            .Where(credit => credit.CatalogItemId == item.Id && credit.Role == role)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            database.CatalogItemMusicArtistCredits.RemoveRange(existing);
            await database.SaveChangesAsync(cancellationToken);
        }

        var position = 0;
        foreach (var name in names)
        {
            var artist = await FindOrCreateMusicArtistAsync(item, name, cancellationToken);
            database.CatalogItemMusicArtistCredits.Add(new CatalogItemMusicArtistCredit
            {
                CatalogItemId = item.Id,
                ArtistCatalogItemId = artist.Id,
                Role = role,
                Position = position++,
            });
        }
        if (position > 0) await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> MusicCreditNamesAsync(
        int itemId,
        string role,
        CancellationToken cancellationToken) =>
        await database.CatalogItemMusicArtistCredits.AsNoTracking()
            .Where(credit => credit.CatalogItemId == itemId && credit.Role == role)
            .OrderBy(credit => credit.Position)
            .Select(credit => credit.ArtistCatalogItem!.Title)
            .ToListAsync(cancellationToken);

    private static IReadOnlyList<string> ParseMusicNames(string value) =>
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // POST /catalog-items/{id}/artwork?kind=poster|backdrop — store a custom image on the
    // MediaPager data disk and point the item's poster/backdrop at it.
    [HttpPost("{id:int}/artwork")]
    [Authorize(Policy = "CanEditCatalogs")]
    public async Task<IActionResult> UploadArtwork(int id, [FromForm] IFormFile file, [FromQuery] string kind = "poster", CancellationToken cancellationToken = default)
    {
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (file is not { Length: > 0 }) return BadRequest(new { error = "An image file is required." });
        if (file.Length > 6 * 1024 * 1024) return BadRequest(new { error = "Image files must be 6 MB or smaller." });

        if (!IsArtworkKind(kind)) return BadRequest(new { error = "Artwork kind must be poster or backdrop." });
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".avif"))
            return BadRequest(new { error = "Unsupported image type. Use PNG, JPG, JPEG, WEBP, GIF or AVIF." });

        var fileName = artwork.Save(await ArtworkDirectoryAsync(kind, cancellationToken), file.OpenReadStream(), extension);
        var url = $"{Request.Scheme}://{Request.Host}/artwork/{ArtworkRouteKind(kind)}/{fileName}";
        SetArtwork(item, kind, url);
        item.ModifiedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        var updated = await LoadAsync(id, cancellationToken);
        return Ok(await ToDtoAsync(updated!, cancellationToken));
    }

    // POST /catalog-items/{id}/artwork-url?kind=poster|backdrop — fetch an image from a
    // remote URL, verify it is really an image, store a local copy under the artwork dir,
    // and point the item at that copy. Artwork should never depend on a third-party host
    // staying up, so this is how URL-based art is ingested instead of being linked to.
    [HttpPost("{id:int}/artwork-url")]
    [Authorize(Policy = "CanEditCatalogs")]
    public async Task<IActionResult> IngestArtwork(int id, ArtworkUrlRequest request, [FromQuery] string kind = "poster", CancellationToken cancellationToken = default)
    {
        if (!IsArtworkKind(kind)) return BadRequest(new { error = "Artwork kind must be poster or backdrop." });
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri))
            return BadRequest(new { error = "A valid image URL is required." });
        if (!await StreamingService.IsSafeStreamUri(uri, cancellationToken))
            return BadRequest(new { error = "Only HTTPS image URLs from public hosts are allowed." });

        using var client = httpClientFactory.CreateClient("upstream");
        using var response = await client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return BadRequest(new { error = $"Could not fetch the image (HTTP {(int)response.StatusCode})." });

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0) return BadRequest(new { error = "That URL returned no data." });
        if (bytes.Length > 6 * 1024 * 1024) return BadRequest(new { error = "Image files must be 6 MB or smaller." });

        var mime = response.Content.Headers.ContentType?.MediaType ?? "";
        var extension = mime switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/avif" => ".avif",
            _ => null,
        } ?? AllowedArtworkExtension(uri.AbsolutePath);
        if (extension is null || !LooksLikeImage(bytes, extension))
            return BadRequest(new { error = "That URL does not point at a supported image." });

        await using var stream = new MemoryStream(bytes);
        var fileName = artwork.Save(await ArtworkDirectoryAsync(kind, cancellationToken), stream, extension);
        var url = $"{Request.Scheme}://{Request.Host}/artwork/{ArtworkRouteKind(kind)}/{fileName}";
        SetArtwork(item, kind, url);
        item.ModifiedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        var updated = await LoadAsync(id, cancellationToken);
        return Ok(await ToDtoAsync(updated!, cancellationToken));
    }

    // PUT /catalog-items/{id}/tags — replace the IMDb-style tag set (Genre, Country,
    // Writer, Director, Producer) wholesale. The whole set is sent so the dialog can just
    // save what it shows; types not in the known list are rejected.
    [HttpPut("{id:int}/tags")]
    [Authorize(Policy = "CanEditCatalogs")]
    public async Task<IActionResult> UpdateTags(int id, UpdateCatalogTagsRequest request, CancellationToken cancellationToken)
    {
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();

        var tags = (request.Tags ?? []).ToList();
        if (tags.Count > 40) return BadRequest(new { error = "Too many tags (max 40)." });

        var normalized = new List<(string Type, string Value)>();
        foreach (var tag in tags)
        {
            var type = (tag.Type ?? "").Trim().ToLowerInvariant();
            var value = (tag.Value ?? "").Trim();
            if (!TagTypes.Contains(type)) return BadRequest(new { error = $"Unknown tag type \"{type}\"." });
            if (value.Length == 0) continue;
            if (value.Length > 60) return BadRequest(new { error = "Tags must be 60 characters or fewer." });
            var pair = (type, value);
            if (!normalized.Contains(pair)) normalized.Add(pair);
        }

        var existing = await database.CatalogItemTags
            .Where(tag => tag.CatalogItemId == id)
            .ToListAsync(cancellationToken);
        database.CatalogItemTags.RemoveRange(existing);
        foreach (var (type, value) in normalized)
            database.CatalogItemTags.Add(new CatalogItemTag { CatalogItemId = id, Type = type, Value = value });
        item.ModifiedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        var updated = await LoadAsync(id, cancellationToken);
        return Ok(await ToDtoAsync(updated!, cancellationToken));
    }

    // PUT /catalog-items/{id}/rating — let the current user set their personal rating
    // (what *they* believe the movie is rated). Any authenticated user can rate, like
    // their nav order; the shared/official rating is edited via PUT /catalog-items/{id}.
    [HttpPut("{id:int}/rating")]
    public async Task<IActionResult> SetRating(int id, UpdateCatalogItemRatingRequest request, CancellationToken cancellationToken)
    {
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (request.Rating is not double rating || rating is < 0 or > 10)
            return BadRequest(new { error = "Rating must be between 0 and 10." });

        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        rating = Math.Round(rating * 2, MidpointRounding.AwayFromZero) / 2;
        var existing = await database.CatalogItemRatings
            .FirstOrDefaultAsync(entry => entry.UserId == userId && entry.CatalogItemId == id, cancellationToken);
        if (rating == 0)
        {
            if (existing is not null) database.CatalogItemRatings.Remove(existing);
        }
        else if (existing is not null)
        {
            existing.Rating = rating;
            existing.ModifiedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            database.CatalogItemRatings.Add(new CatalogItemRating
            {
                UserId = userId!,
                CatalogItemId = id,
                Rating = rating,
            });
        }
        await database.SaveChangesAsync(cancellationToken);

        return Ok(new { rating = rating == 0 ? (double?)null : rating });
    }

    // DELETE /catalog-items/{id}/rating — clear the current user's rating.
    [HttpDelete("{id:int}/rating")]
    public async Task<IActionResult> ClearRating(int id, CancellationToken cancellationToken)
    {
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var existing = await database.CatalogItemRatings
            .FirstOrDefaultAsync(entry => entry.UserId == userId && entry.CatalogItemId == id, cancellationToken);
        if (existing is not null)
        {
            database.CatalogItemRatings.Remove(existing);
            await database.SaveChangesAsync(cancellationToken);
        }
        return Ok(new { rating = (double?)null });
    }

    private static void SetArtwork(CatalogItem item, string kind, string url)
    {
        if (kind.Equals("backdrop", StringComparison.OrdinalIgnoreCase)) item.BackdropUrl = url;
        else item.ImageUrl = url;
    }

    private static bool IsArtworkKind(string kind) =>
        kind.Equals("poster", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("backdrop", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("background", StringComparison.OrdinalIgnoreCase);

    private static string ArtworkRouteKind(string kind) =>
        kind.Equals("poster", StringComparison.OrdinalIgnoreCase) ? "poster" : "background";

    // POST /catalog-items/{id}/local — mint a short-lived capability URL for playing the
    // item's file on disk (the media element can't carry our auth token).
    [HttpPost("{id:int}/local")]
    public async Task<IActionResult> LocalPlay(int id, CancellationToken cancellationToken)
    {
        var item = await database.CatalogItems.AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (string.IsNullOrWhiteSpace(item.StoragePath) || !System.IO.File.Exists(item.StoragePath))
            return BadRequest(new { error = "This item has no local file to play." });

        var token = localSessions.Create(item.StoragePath);
        return Ok(new { url = $"{Request.Scheme}://{Request.Host}/local/{token}", mime = MimeFor(item.StoragePath) });
    }

    // DELETE /catalog-items/{id} — remove a library entry; the file itself stays on disk.
    [HttpDelete("{id:int}")]
    [Authorize(Policy = "CanEditCatalogs")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();
        var candidateIds = await database.CatalogItemMusicArtistCredits.AsNoTracking()
            .Where(credit => credit.CatalogItemId == item.Id)
            .Select(credit => credit.ArtistCatalogItemId)
            .ToListAsync(cancellationToken);
        if (item.ParentId is int parentId) candidateIds.Add(parentId);
        database.CatalogItems.Remove(item);
        await database.SaveChangesAsync(cancellationToken);
        await MusicCatalogHierarchy.PruneEmptyAncestorsAsync(database, candidateIds, cancellationToken);
        return NoContent();
    }

    // GET /local/{capability} — [AllowAnonymous] playback of a local file. Validation is
    // the unguessable, expiring token minted by LocalPlay (same model as /stream sessions).
    [HttpGet("/local/{capability}")]
    [AllowAnonymous]
    public IActionResult ServeLocal(string capability)
    {
        var path = localSessions.TryGet(capability);
        if (path is null) return NotFound();
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, MimeFor(path), enableRangeProcessing: true);
    }

    // GET /artwork/{fileName} — [AllowAnonymous] image serving for poster/backdrop <img>
    // tags (which can't carry our bearer token). The folder is the Artwork:Directory
    // runtime setting, resolved per request so it can be changed at runtime in Settings.
    [HttpGet("/artwork/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> ServeArtwork(string fileName, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(fileName);
        if (!string.Equals(name, fileName, StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
            return BadRequest();
        var candidates = new[]
        {
            await LegacyArtworkDirectoryAsync(cancellationToken),
            await ArtworkDirectoryAsync("poster", cancellationToken),
            await ArtworkDirectoryAsync("backdrop", cancellationToken),
        }.Distinct(StringComparer.Ordinal).Select(directory => Path.Combine(directory, name));
        var path = candidates.FirstOrDefault(System.IO.File.Exists);
        return path is null ? NotFound() : PhysicalFile(path, MimeForImage(name));
    }

    [HttpGet("/artwork/{kind}/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> ServeCategorizedArtwork(string kind, string fileName, CancellationToken cancellationToken)
    {
        if (!kind.Equals("poster", StringComparison.OrdinalIgnoreCase) &&
            !kind.Equals("background", StringComparison.OrdinalIgnoreCase))
            return NotFound();
        var name = Path.GetFileName(fileName);
        if (!string.Equals(name, fileName, StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
            return BadRequest();
        var directory = await ArtworkDirectoryAsync(kind, cancellationToken);
        var path = Path.Combine(directory, name);
        return !System.IO.File.Exists(path) ? NotFound() : PhysicalFile(path, MimeForImage(name));
    }

    private async Task<string> ArtworkDirectoryAsync(string kind, CancellationToken cancellationToken)
    {
        var key = kind.Equals("poster", StringComparison.OrdinalIgnoreCase)
            ? RuntimeSettingKeys.ArtworkPosterDirectory
            : RuntimeSettingKeys.ArtworkBackgroundDirectory;
        var specific = await settings.GetAsync(key, cancellationToken);
        var legacy = await settings.GetAsync(RuntimeSettingKeys.ArtworkDirectory, cancellationToken);
        return ArtworkStorage.ResolveCategoryDirectory(specific, legacy, storageLayout.DataRoot,
            poster: kind.Equals("poster", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> LegacyArtworkDirectoryAsync(CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(RuntimeSettingKeys.ArtworkDirectory, cancellationToken);
        if (!string.IsNullOrWhiteSpace(configured)) return ArtworkStorage.ResolveDirectory(configured);
        var migrated = Path.Combine(storageLayout.DataRoot, "artwork", "legacy");
        return Directory.Exists(migrated) ? migrated : ArtworkStorage.LegacyDefaultDirectory();
    }

    private async Task<CatalogItem?> LoadAsync(int id, CancellationToken cancellationToken) =>
        await database.CatalogItems.AsNoTracking()
            .Include(item => item.Tags)
            .Include(item => item.Parent)
            .Include(item => item.Catalog!)
            .ThenInclude(catalog => catalog!.CatalogType!)
            .ThenInclude(type => type!.MediaType!)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    private async Task<List<object>> ToDtosAsync(List<CatalogItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return [];
        var ids = items.Select(item => item.Id).ToList();
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var tags = await database.CatalogItemTags.AsNoTracking()
            .Where(tag => ids.Contains(tag.CatalogItemId))
            .ToListAsync(cancellationToken);
        var ratings = await database.CatalogItemRatings.AsNoTracking()
            .Where(rating => ids.Contains(rating.CatalogItemId) && rating.UserId == userId)
            .ToListAsync(cancellationToken);
        var credits = await database.CatalogItemMusicArtistCredits.AsNoTracking()
            .Include(credit => credit.ArtistCatalogItem)
            .Where(credit => ids.Contains(credit.CatalogItemId))
            .OrderBy(credit => credit.Position)
            .ToListAsync(cancellationToken);
        var creditsByItem = credits.GroupBy(credit => credit.CatalogItemId)
            .ToDictionary(group => group.Key, group => group.Select(credit => new MusicArtistCreditResponse(
                credit.ArtistCatalogItemId,
                credit.ArtistCatalogItem!.Title,
                credit.ArtistCatalogItem.MusicBrainzId,
                credit.Role,
                credit.Position)).ToList());
        return items.Select(item => ToDto(item, tags, ratings,
            creditsByItem.GetValueOrDefault(item.Id))).ToList();
    }

    private async Task<object> ToDtoAsync(CatalogItem item, CancellationToken cancellationToken)
    {
        var dto = await ToDtosAsync([item], cancellationToken);
        return dto[0];
    }

    private static object ToDto(
        CatalogItem item,
        List<CatalogItemTag>? tags = null,
        List<CatalogItemRating>? ratings = null,
        List<MusicArtistCreditResponse>? musicCredits = null) => new
    {
        item.Id,
        item.CatalogId,
        CatalogName = item.Catalog?.Name,
        CatalogTypeId = item.Catalog?.CatalogTypeId,
        CatalogTypeSlug = item.Catalog?.CatalogType?.Slug,
        MediaTypeSlug = item.Catalog?.CatalogType?.MediaType?.Slug,
        item.ParentId,
        ParentTitle = item.Parent?.Title,
        item.Title,
        item.SortTitle,
        item.Kind,
        item.Overview,
        item.StoragePath,
        item.SizeBytes,
        item.DurationSeconds,
        item.Year,
        item.OriginalTitle,
        item.OriginalAvailableAt,
        item.ContentRating,
        item.Rating,
        item.SeasonNumber,
        item.EpisodeNumber,
        item.ImageUrl,
        item.BackdropUrl,
        item.ExternalId,
        item.ImdbId,
        item.Isbn,
        item.MusicBrainzId,
        item.Isrc,
        item.TrackNumber,
        item.TrackCount,
        item.DiscNumber,
        item.DiscCount,
        MusicArtistCredits = musicCredits ?? [],
        Tags = (tags ?? item.Tags).Select(tag => new { tag.Type, tag.Value }).OrderBy(tag => tag.Type).ThenBy(tag => tag.Value),
        UserRating = ratings?.FirstOrDefault(rating => rating.CatalogItemId == item.Id)?.Rating,
        item.CreatedAt,
        item.ModifiedAt,
    };

    private sealed record MusicArtistCreditResponse(
        int ArtistCatalogItemId,
        string Name,
        string? MusicBrainzId,
        string Role,
        int Position);

    private static string? NormalizeUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? CleanIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SafeUploadFileName(string originalName, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var name = new string(originalName.Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = $"upload{extension}";
        if (!string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase))
            name += extension;
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name))) name = $"upload{extension}";
        return name;
    }

    private static string UniqueUploadPath(string root, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var suffix = 1; suffix < 10000; suffix++)
        {
            var candidate = suffix == 1 ? fileName : $"{stem} ({suffix}){extension}";
            var path = Path.GetFullPath(Path.Combine(root, candidate));
            if (!System.IO.File.Exists(path) && !Directory.Exists(path)) return path;
        }
        throw new IOException("Could not choose a unique filename in the catalog folder.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch { /* cleanup must not hide the original upload error */ }
    }

    private static string? AllowedArtworkExtension(string urlPath)
    {
        var extension = Path.GetExtension(urlPath).ToLowerInvariant();
        return extension switch
        {
            ".png" => ".png",
            ".jpg" or ".jpeg" => ".jpg",
            ".webp" => ".webp",
            ".gif" => ".gif",
            ".avif" => ".avif",
            _ => null,
        };
    }

    // Light signature check so a URL advertised as an image but serving HTML/text is
    // rejected instead of stored as junk on disk.
    private static bool LooksLikeImage(byte[] bytes, string extension)
    {
        if (bytes.Length < 4) return false;
        if (extension == ".jpg")
            return bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        if (extension == ".png")
            return bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';
        if (extension == ".gif")
            return bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F';
        if (extension == ".webp")
            return bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
                bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
        if (extension == ".avif")
            return bytes.Length >= 12 && bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p' &&
                bytes[8] == (byte)'a' && bytes[9] == (byte)'v';
        return false;
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".ts" => "video/mp2t",
        ".mpeg" or ".mpg" => "video/mpeg",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".m4b" => "audio/mp4",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".wma" => "audio/x-ms-wma",
        ".pdf" => "application/pdf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".rtf" => "application/rtf",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".odp" => "application/vnd.oasis.opendocument.presentation",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
        _ => "application/octet-stream",
    };

    private static string MimeForImage(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        _ => "application/octet-stream",
    };
}

public sealed record UpdateCatalogItemRequest(
    string? Title,
    string? Overview,
    string? ImageUrl,
    string? BackdropUrl,
    int? Year,
    string? SortTitle = null,
    string? OriginalTitle = null,
    string? OriginalAvailableAt = null,
    string? ContentRating = null,
    double? Rating = null,
    string? Artist = null,
    string? Album = null,
    string? ExternalId = null,
    string? ImdbId = null,
    string? Isbn = null,
    string? MusicBrainzId = null,
    string? Isrc = null);

public sealed record ArtworkUrlRequest(string? Url);

public sealed class UploadCatalogItemRequest
{
    public int CatalogId { get; set; }
    public IFormFile? File { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? ExternalId { get; set; }
    public string? ImageUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? OriginalTitle { get; set; }
    public string? OriginalAvailableAt { get; set; }
    public string? ContentRating { get; set; }
    public double? Rating { get; set; }
    public string? ImdbId { get; set; }
    public string? Isbn { get; set; }
    public string? MusicBrainzId { get; set; }
    public string? Isrc { get; set; }
    public string[]? Genres { get; set; }
}

public sealed record CatalogItemTagRequest(string Type, string Value);

public sealed record UpdateCatalogTagsRequest(List<CatalogItemTagRequest>? Tags);

public sealed record UpdateCatalogItemRatingRequest(double? Rating);
