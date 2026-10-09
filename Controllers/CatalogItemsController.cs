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

    // GET /catalog-items — library entries for the catalog tabs. Filter by catalog,
    // kind, or external id (playback uses externalId to find a local copy of a TMDB movie).
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? catalogId,
        [FromQuery] string? kind,
        [FromQuery] string? externalId,
        CancellationToken cancellationToken)
    {
        var query = database.CatalogItems.AsNoTracking()
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
        var item = await database.CatalogItems.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (item is null) return NotFound();

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
        item.ModifiedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        var updated = await LoadAsync(id, cancellationToken);
        return Ok(await ToDtoAsync(updated!, cancellationToken));
    }

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

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".avif"))
            return BadRequest(new { error = "Unsupported image type. Use PNG, JPG, JPEG, WEBP, GIF or AVIF." });

        var fileName = artwork.Save(await ArtworkDirectoryAsync(cancellationToken), file.OpenReadStream(), extension);
        var url = $"{Request.Scheme}://{Request.Host}/artwork/{fileName}";
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
        var fileName = artwork.Save(await ArtworkDirectoryAsync(cancellationToken), stream, extension);
        var url = $"{Request.Scheme}://{Request.Host}/artwork/{fileName}";
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
        database.CatalogItems.Remove(item);
        await database.SaveChangesAsync(cancellationToken);
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
        var path = Path.Combine(await ArtworkDirectoryAsync(cancellationToken), name);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, MimeForImage(name));
    }

    private async Task<string> ArtworkDirectoryAsync(CancellationToken cancellationToken) =>
        ArtworkStorage.ResolveDirectory(await settings.GetAsync(RuntimeSettingKeys.ArtworkDirectory, cancellationToken));

    private async Task<CatalogItem?> LoadAsync(int id, CancellationToken cancellationToken) =>
        await database.CatalogItems.AsNoTracking()
            .Include(item => item.Tags)
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
        return items.Select(item => ToDto(item, tags, ratings)).ToList();
    }

    private async Task<object> ToDtoAsync(CatalogItem item, CancellationToken cancellationToken)
    {
        var dto = await ToDtosAsync([item], cancellationToken);
        return dto[0];
    }

    private static object ToDto(CatalogItem item, List<CatalogItemTag>? tags = null, List<CatalogItemRating>? ratings = null) => new
    {
        item.Id,
        item.CatalogId,
        CatalogName = item.Catalog?.Name,
        CatalogTypeId = item.Catalog?.CatalogTypeId,
        CatalogTypeSlug = item.Catalog?.CatalogType?.Slug,
        MediaTypeSlug = item.Catalog?.CatalogType?.MediaType?.Slug,
        item.ParentId,
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
        Tags = (tags ?? item.Tags).Select(tag => new { tag.Type, tag.Value }).OrderBy(tag => tag.Type).ThenBy(tag => tag.Value),
        UserRating = ratings?.FirstOrDefault(rating => rating.CatalogItemId == item.Id)?.Rating,
        item.CreatedAt,
        item.ModifiedAt,
    };

    private static string? NormalizeUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

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
        ".m4a" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
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
    double? Rating = null);

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
    public string[]? Genres { get; set; }
}

public sealed record CatalogItemTagRequest(string Type, string Value);

public sealed record UpdateCatalogTagsRequest(List<CatalogItemTagRequest>? Tags);

public sealed record UpdateCatalogItemRatingRequest(double? Rating);
