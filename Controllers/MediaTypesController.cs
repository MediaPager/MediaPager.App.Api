using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("media-types")]
public sealed class MediaTypesController(AuthDbContext database) : ControllerBase
{
    // Read-only lookup: seeded at startup (Video, Audio, Book).
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var mediaTypes = await database.MediaTypes
            .AsNoTracking()
            .OrderBy(mediaType => mediaType.Name)
            .ToListAsync(cancellationToken);
        return Ok(mediaTypes);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var mediaType = await database.MediaTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return mediaType is null ? NotFound() : Ok(mediaType);
    }
}
