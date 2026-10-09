using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("catalog-types")]
public sealed class CatalogTypesController(AuthDbContext database) : ControllerBase
{
    // Read-only lookup: seeded at startup (Movies, TV Shows, Music, ...).
    // Each entry carries its MediaType (Video, Audio, Book) for the client.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var catalogTypes = await database.CatalogTypes
            .AsNoTracking()
            .Include(item => item.MediaType)
            .OrderBy(item => item.Name)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Slug,
                MediaType = new
                {
                    item.MediaType!.Id,
                    item.MediaType.Name,
                    item.MediaType.Slug,
                },
            })
            .ToListAsync(cancellationToken);
        return Ok(catalogTypes);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var catalogType = await database.CatalogTypes
            .AsNoTracking()
            .Include(item => item.MediaType)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (catalogType is null) return NotFound();
        return Ok(new
        {
            catalogType.Id,
            catalogType.Name,
            catalogType.Slug,
            MediaType = new
            {
                catalogType.MediaType!.Id,
                catalogType.MediaType.Name,
                catalogType.MediaType.Slug,
            },
        });
    }
}
