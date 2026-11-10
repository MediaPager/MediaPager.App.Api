using System.Buffers.Binary;
using System.Text;
using MediaPager.App.Api.Services;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class Mp4FastStartDetectorTests
{
    [Fact]
    public async Task Detects_movie_metadata_after_media_data()
    {
        var path = await WriteBoxesAsync("ftyp", "mdat", "moov");
        try
        {
            Assert.True(await Mp4FastStartDetector.RequiresFastStartAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Keeps_files_with_movie_metadata_before_media_data()
    {
        var path = await WriteBoxesAsync("ftyp", "moov", "mdat");
        try
        {
            Assert.False(await Mp4FastStartDetector.RequiresFastStartAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Invalid_atom_sizes_are_not_rewritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mediapager-mp4-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [0, 0, 0, 4, (byte)'m', (byte)'d', (byte)'a', (byte)'t']);
        try
        {
            Assert.False(await Mp4FastStartDetector.RequiresFastStartAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteBoxesAsync(params string[] types)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mediapager-mp4-{Guid.NewGuid():N}.mp4");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var type in types)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)header.Length);
            Encoding.ASCII.GetBytes(type, header.AsSpan(4));
            await stream.WriteAsync(header);
        }
        return path;
    }
}
