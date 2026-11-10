using System.Buffers.Binary;
using System.Text;

namespace MediaPager.App.Api.Services;

internal static class Mp4FastStartDetector
{
    public static async Task<bool> RequiresFastStartAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[16];
        long? movieMetadataOffset = null;
        long? mediaDataOffset = null;

        while (stream.Position <= stream.Length - 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var boxOffset = stream.Position;
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), cancellationToken);

            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            long headerSize = 8;
            long boxSize;
            if (size32 == 1)
            {
                await stream.ReadExactlyAsync(header.AsMemory(8, 8), cancellationToken);
                var extendedSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                if (extendedSize > long.MaxValue) return false;
                boxSize = (long)extendedSize;
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = stream.Length - boxOffset;
            }
            else
            {
                boxSize = size32;
            }

            if (boxSize < headerSize || boxSize > stream.Length - boxOffset)
                return false;

            if (type == "moov") movieMetadataOffset = boxOffset;
            if (type == "mdat") mediaDataOffset = boxOffset;
            if (movieMetadataOffset is long moov && mediaDataOffset is long mdat)
                return moov > mdat;

            stream.Position = boxOffset + boxSize;
        }

        return false;
    }
}
