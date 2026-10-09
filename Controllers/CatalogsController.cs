using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("catalogs")]
public sealed class CatalogsController(AuthDbContext database, ILogger<CatalogsController> logger) : ControllerBase
{
    // GET /catalogs/nav — the current user's left-nav catalog list. Ordering is per-user:
    // catalogs the user has explicitly ordered come first (in that order); everything else
    // is appended in creation order. (Stream is a virtual, always-pinned tab handled by the
    // client, not a catalog.)
    [HttpGet("nav")]
    public async Task<IActionResult> Nav(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        var catalogs = await database.Catalogs
            .AsNoTracking()
            .Include(item => item.CatalogType)
            .ThenInclude(type => type!.MediaType)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);

        var overrides = (await database.CatalogOrders
                .AsNoTracking()
                .Where(order => order.UserId == userId)
                .ToListAsync(cancellationToken))
            .GroupBy(order => order.CatalogId)
            .ToDictionary(group => group.Key, group => group.Min(order => order.SortOrder));

        // No overrides → plain creation order.
        if (overrides.Count == 0)
            return Ok(catalogs.Select(ToDto).ToList());

        var ordered = catalogs
            .Where(item => overrides.ContainsKey(item.Id))
            .OrderBy(item => overrides[item.Id])
            .ThenBy(item => item.Id)
            .Concat(catalogs.Where(item => !overrides.ContainsKey(item.Id)));
        return Ok(ordered.Select(ToDto).ToList());
    }

    // PUT /catalogs/nav — save the user's preferred nav ordering. The payload is the full
    // ordered list of catalog ids as shown in the nav; any catalog missing from the payload
    // (e.g. one created after the user last saved) is appended at the bottom and saved too,
    // so a fresh catalog never disappears from the nav.
    [HttpPut("nav")]
    public async Task<IActionResult> SaveNav(NavOrderRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null) return Unauthorized();

        var validIds = (request.CatalogIds ?? [])
            .Distinct()
            .Where(id => id > 0)
            .ToList();
        var existing = await database.Catalogs
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var existingIds = existing.ToHashSet();

        // Payload first (existing ones only), then anything else appended in creation order.
        var ids = validIds.Where(existingIds.Contains).ToList();
        ids.AddRange(existing.Where(id => !ids.Contains(id)));

        await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var stale = await database.CatalogOrders
                .Where(order => order.UserId == userId)
                .ToListAsync(cancellationToken);
            database.CatalogOrders.RemoveRange(stale);

            for (var index = 0; index < ids.Count; index++)
            {
                database.CatalogOrders.Add(new CatalogOrder
                {
                    UserId = userId,
                    CatalogId = ids[index],
                    SortOrder = index,
                });
            }
            await database.SaveChangesAsync(cancellationToken);
            await database.Database.CommitTransactionAsync(cancellationToken);
        }
        catch
        {
            await database.Database.RollbackTransactionAsync(cancellationToken);
            throw;
        }

        // Re-read and return the now-persisted order.
        var catalogs = await database.Catalogs
            .AsNoTracking()
            .Include(item => item.CatalogType)
            .ThenInclude(type => type!.MediaType)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var byId = catalogs.ToDictionary(item => item.Id);
        return Ok(ids.Where(byId.ContainsKey).Select(id => ToDto(byId[id])).ToList());
    }

    // GET /catalogs — every catalog, with its catalog type and derived media type.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var catalogs = await database.Catalogs
            .AsNoTracking()
            .Include(item => item.CatalogType)
            .ThenInclude(type => type!.MediaType)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return Ok(catalogs.Select(ToDto).ToList());
    }

    // GET /catalogs/{id} — a single catalog by id.
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs
            .AsNoTracking()
            .Include(item => item.CatalogType)
            .ThenInclude(type => type!.MediaType)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return catalog is null ? NotFound() : Ok(ToDto(catalog));
    }

    // POST /catalogs — create a catalog (e.g. "My Movies" → Movies → Video).
    // Mutations require admin:catalogs-edit (admin:super always passes).
    [HttpPost]
    [Authorize(Policy = "CanEditCatalogs")]
    public async Task<IActionResult> Create(UpsertCatalogRequest request, CancellationToken cancellationToken)
    {
        if (!TryNormalize(request, out var name, out var description, out var path, out var error))
            return BadRequest(new { error });

        var catalogType = await database.CatalogTypes.FindAsync([request.CatalogTypeId], cancellationToken);
        if (catalogType is null)
            return BadRequest(new { error = $"Catalog type {request.CatalogTypeId} does not exist." });

        var catalog = new Catalog
        {
            Name = name,
            Description = description,
            CatalogTypeId = catalogType.Id,
            Path = path,
        };
        database.Catalogs.Add(catalog);
        await database.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = catalog.Id }, await LoadDtoAsync(catalog.Id, cancellationToken));
    }

    // PUT /catalogs/{id} — replace a catalog's mutable fields.
    [HttpPut("{id:int}")]
    [Authorize(Policy = "CanEditCatalogs")]
    public async Task<IActionResult> Update(int id, UpsertCatalogRequest request, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.FirstOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (catalog is null) return NotFound();

        if (!TryNormalize(request, out var name, out var description, out var path, out var error))
            return BadRequest(new { error });

        var catalogType = await database.CatalogTypes.FindAsync([request.CatalogTypeId], cancellationToken);
        if (catalogType is null)
            return BadRequest(new { error = $"Catalog type {request.CatalogTypeId} does not exist." });

        catalog.Name = name;
        catalog.Description = description;
        catalog.CatalogTypeId = catalogType.Id;
        catalog.Path = path;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(await LoadDtoAsync(catalog.Id, cancellationToken));
    }

    // DELETE /catalogs/{id}?purge=true — remove a catalog. Without purge only the DB row is
    // removed; with purge the *contents* of Catalog.Path are recursively deleted first.
    [HttpDelete("{id:int}")]
    [Authorize(Policy = "CanEditCatalogs")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool purge = false, CancellationToken cancellationToken = default)
    {
        var catalog = await database.Catalogs.FirstOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (catalog is null) return NotFound();

        string? purgedPath = null;
        if (purge && !string.IsNullOrWhiteSpace(catalog.Path))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(catalog.Path);
            }
            catch (Exception)
            {
                return BadRequest(new { error = "The catalog path is not a valid filesystem path." });
            }

            var guardError = GuardPurgePath(fullPath);
            if (guardError is not null) return BadRequest(new { error = guardError });

            if (Directory.Exists(fullPath))
            {
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else System.IO.File.Delete(entry);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Purging catalog {CatalogId} data at {Path} failed.", catalog.Id, fullPath);
                    return Problem($"Could not delete all data at {fullPath}: {exception.Message}");
                }
                purgedPath = fullPath;
            }
            logger.LogWarning("Catalog {CatalogId} ({Name}) purged data at {Path}.", catalog.Id, catalog.Name, fullPath);
        }

        database.Catalogs.Remove(catalog);
        var staleOrders = await database.CatalogOrders
            .Where(order => order.CatalogId == id)
            .ToListAsync(cancellationToken);
        database.CatalogOrders.RemoveRange(staleOrders);
        await database.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            message = purgedPath is null
                ? "Catalog deleted."
                : $"Catalog and all data under {purgedPath} were deleted.",
            purgedPath,
        });
    }

    // Refuse obviously catastrophic purge targets: filesystem roots, the home folder,
    // anything containing the home folder, and MediaPager's own data directory.
    private static string? GuardPurgePath(string fullPath)
    {
        static string Normalize(string path) =>
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Path.IsPathRooted(fullPath))
            return "The catalog path must be absolute to delete data.";

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || string.Equals(Normalize(fullPath), Normalize(root), StringComparison.OrdinalIgnoreCase))
            return "Refusing to delete a filesystem root.";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            home = Normalize(Path.GetFullPath(home));
            // Refuse the home folder itself and any folder *containing* it (e.g. /Users).
            // A catalog inside home (/Users/me/Media) is fine.
            if (IsWithin(home, fullPath))
                return "Refusing to delete your home folder or a folder that contains it.";
        }

        var appData = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaPager")
            : Path.Combine(home, ".MediaPager");
        if (IsWithin(appData, fullPath) || IsWithin(fullPath, appData))
            return "Refusing to delete MediaPager's own data folder (auth database and signing key live there).";

        return null;
    }

    // True when candidate is target itself or lives under it.
    private static bool IsWithin(string candidate, string target)
    {
        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(target)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedCandidate, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<object?> LoadDtoAsync(int id, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs
            .AsNoTracking()
            .Include(item => item.CatalogType)
            .ThenInclude(type => type!.MediaType)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return catalog is null ? null : ToDto(catalog);
    }

    // The media type is always derived: Catalog → CatalogType → MediaType.
    private static object ToDto(Catalog item) => new
    {
        item.Id,
        item.Name,
        item.Description,
        item.Path,
        CatalogType = new
        {
            item.CatalogType!.Id,
            item.CatalogType.Name,
            item.CatalogType.Slug,
            MediaType = new
            {
                item.CatalogType.MediaType!.Id,
                item.CatalogType.MediaType.Name,
                item.CatalogType.MediaType.Slug,
            },
        },
    };

    private static bool TryNormalize(
        UpsertCatalogRequest request,
        out string name,
        out string description,
        out string? path,
        out string error)
    {
        name = request.Name?.Trim() ?? "";
        description = request.Description?.Trim() ?? "";
        path = string.IsNullOrWhiteSpace(request.Path) ? null : request.Path.Trim();
        error = "";

        if (name.Length == 0)
        {
            error = "A name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "A folder path is required — every catalog points at a folder for its media.";
            return false;
        }

        return true;
    }
}

public sealed record UpsertCatalogRequest(string? Name, string? Description, int CatalogTypeId, string? Path);

public sealed record NavOrderRequest(List<int>? CatalogIds);
