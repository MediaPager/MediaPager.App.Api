using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("catalogs/{catalogId:int}/docs")]
[Authorize]
public sealed class DocsFilesController(AuthDbContext database) : ControllerBase
{
    private const long MaxDocumentBytes = 1L * 1024 * 1024 * 1024;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    [HttpGet]
    public async Task<IActionResult> List(int catalogId, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        var (root, error) = await ResolveDocsRootAsync(catalogId, cancellationToken);
        if (error is not null) return error;
        var directory = ResolveContainedPath(root!, path);
        if (directory is null) return BadRequest(new { error = "The requested folder is outside this Docs catalog." });
        if (!Directory.Exists(directory)) return NotFound(new { error = "Folder not found." });
        if (HasReparsePointBetween(root!, directory)) return BadRequest(new { error = "Linked folders cannot be browsed from a Docs catalog." });

        var entries = Directory.EnumerateFileSystemEntries(directory)
            .Select(entry => new FileInfoOrDirectory(entry))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(2000)
            .Select(entry => new DocsFileEntry(
                entry.Name,
                Path.GetRelativePath(root!, entry.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                entry.IsDirectory,
                entry.IsDirectory ? null : entry.Length,
                entry.ModifiedAt))
            .ToArray();
        return Ok(entries);
    }

    [HttpGet("content/{**path}")]
    public async Task<IActionResult> Read(int catalogId, string path, CancellationToken cancellationToken)
    {
        var (root, error) = await ResolveDocsRootAsync(catalogId, cancellationToken);
        if (error is not null) return error;
        var file = ResolveContainedPath(root!, path);
        if (file is null) return BadRequest(new { error = "The requested file is outside this Docs catalog." });
        if (HasReparsePointBetween(root!, file)) return BadRequest(new { error = "Linked files cannot be opened from a Docs catalog." });
        if (!System.IO.File.Exists(file)) return NotFound(new { error = "File not found." });

        var type = ContentTypes.TryGetContentType(file, out var contentType) ? contentType : "application/octet-stream";
        return PhysicalFile(file, type, enableRangeProcessing: true);
    }

    [HttpPut("content/{**path}")]
    [Authorize(Policy = "CanEditCatalogs")]
    [RequestSizeLimit(MaxDocumentBytes)]
    public async Task<IActionResult> Write(int catalogId, string path, CancellationToken cancellationToken)
    {
        var (root, error) = await ResolveDocsRootAsync(catalogId, cancellationToken);
        if (error is not null) return error;
        var file = ResolveContainedPath(root!, path);
        if (file is null) return BadRequest(new { error = "The requested file is outside this Docs catalog." });
        if (HasReparsePointBetween(root!, file)) return BadRequest(new { error = "Linked files cannot be written from a Docs catalog." });
        if (!Directory.Exists(Path.GetDirectoryName(file))) return NotFound(new { error = "Destination folder not found." });

        var temporary = Path.Combine(Path.GetDirectoryName(file)!, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await Request.Body.CopyToAsync(output, cancellationToken);
            System.IO.File.Move(temporary, file, overwrite: true);
            return NoContent();
        }
        finally
        {
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        }
    }

    private async Task<(string? Root, IActionResult? Error)> ResolveDocsRootAsync(int catalogId, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.AsNoTracking()
            .Include(entry => entry.CatalogType)
            .FirstOrDefaultAsync(entry => entry.Id == catalogId, cancellationToken);
        if (catalog is null) return (null, NotFound(new { error = "Docs catalog not found." }));
        if (!string.Equals(catalog.CatalogType?.Slug, "docs", StringComparison.OrdinalIgnoreCase))
            return (null, BadRequest(new { error = "File browsing is only available for Docs catalogs." }));
        if (string.IsNullOrWhiteSpace(catalog.Path)) return (null, BadRequest(new { error = "Set a folder path for this Docs catalog first." }));
        try
        {
            var root = Path.GetFullPath(catalog.Path);
            return Directory.Exists(root)
                ? (root, null)
                : (null, NotFound(new { error = "Docs catalog folder not found." }));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return (null, BadRequest(new { error = "Docs catalog path is invalid." }));
        }
    }

    private static string? ResolveContainedPath(string root, string? relativePath)
    {
        var relative = (relativePath ?? "").Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative)) return null;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        return fullPath.Equals(fullRoot, StringComparison.Ordinal) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? fullPath
            : null;
    }

    private static bool HasReparsePointBetween(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = Path.GetFullPath(root);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) && !System.IO.File.Exists(current)) continue;
            if ((System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private sealed class FileInfoOrDirectory
    {
        private readonly FileSystemInfo _info;
        public FileInfoOrDirectory(string path) => _info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        public string Name => _info.Name;
        public string FullName => _info.FullName;
        public bool IsDirectory => _info is DirectoryInfo;
        public long Length => _info is FileInfo file ? file.Length : 0;
        public DateTime ModifiedAt => _info.LastWriteTimeUtc;
    }
}

public sealed record DocsFileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTime ModifiedAt);
