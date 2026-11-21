using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using static Lodestone.Core.Assets.CharacterModel;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

internal static class CharacterTexture
{
    private const int PreviewLimit = 1024;
    private const int PixelLimit = 4096 * 4096;

    internal static byte[] ToPng(byte[] data, bool nearest, CancellationToken token)
    {
        var (pixels, width, height) = Decode(data, token);
        if (Math.Max(width, height) > PreviewLimit)
            (pixels, width, height) = Resize(pixels, width, height, nearest, token);
        return WritePng(pixels, width, height, token);
    }

    internal static (byte[] Pixels, int Width, int Height) Decode(byte[] data, CancellationToken token)
    {
        var format = U32(data, 4);
        var width = (int)U16(data, 8);
        var height = (int)U16(data, 10);
        var start = Offset(data, 28);
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || width * height > PixelLimit || start < 80)
            throw new SqPackException("TEX 尺寸或像素偏移超出限制");
        token.ThrowIfCancellationRequested();
        var compression = format switch
        {
            0x3420 => CompressionFormat.Bc1WithAlpha, 0x3430 => CompressionFormat.Bc2,
            0x3431 => CompressionFormat.Bc3, 0x6120 => CompressionFormat.Bc4,
            0x6230 => CompressionFormat.Bc5, 0x6432 => CompressionFormat.Bc7,
            _ => CompressionFormat.Unknown
        };
        if (compression != CompressionFormat.Unknown)
        {
            var blockSize = format is 0x3420 or 0x6120 ? 8 : 16;
            var length = ((width + 3) / 4) * ((height + 3) / 4) * blockSize;
            using var stream = new MemoryStream(data, start, Field(data, start, length).Length, writable: false);
            var decoder = new BcDecoder();
            var rgba = decoder.DecodeRaw(stream, width, height, compression);
            var decoded = MemoryMarshal.AsBytes(rgba.AsSpan()).ToArray();
            if (format == 0x6120)
                for (var i = 0; i < decoded.Length; i += 4)
                    decoded[i + 1] = decoded[i + 2] = decoded[i];
            return (decoded, width, height);
        }

        var stride = format switch
        {
            0x1450 or 0x1451 => 4, 0x1440 => 2, 0x1130 or 0x1131 => 1,
            _ => throw new SqPackException($"未支持的 TEX 格式 0x{format:x}")
        };
        var source = Field(data, start, width * height * stride);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * stride;
                var index = (y * width + x) * 4;
                switch (format)
                {
                    case 0x1450:
                    case 0x1451:
                        pixels[index] = source[i + 2];
                        pixels[index + 1] = source[i + 1];
                        pixels[index + 2] = source[i];
                        pixels[index + 3] = format == 0x1451 ? (byte)255 : source[i + 3];
                        break;
                    case 0x1440:
                        var value = U16(source, i);
                        pixels[index] = (byte)(((value >> 8) & 15) * 17);
                        pixels[index + 1] = (byte)(((value >> 4) & 15) * 17);
                        pixels[index + 2] = (byte)((value & 15) * 17);
                        pixels[index + 3] = (byte)((value >> 12) * 17);
                        break;
                    default:
                        pixels[index] = pixels[index + 1] = pixels[index + 2] = format == 0x1130 ? source[i] : (byte)255;
                        pixels[index + 3] = format == 0x1131 ? source[i] : (byte)255;
                        break;
                }
            }
        }
        return (pixels, width, height);
    }

    private static (byte[] Pixels, int Width, int Height) Resize(byte[] pixels, int width, int height, bool nearest,
        CancellationToken token)
    {
        var scale = (double)PreviewLimit / Math.Max(width, height);
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        var output = new byte[targetWidth * targetHeight * 4];
        var horizontal = (double)width / targetWidth;
        var vertical = (double)height / targetHeight;
        for (var y = 0; y < targetHeight; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < targetWidth; x++)
            {
                var destination = output.AsSpan((y * targetWidth + x) * 4, 4);
                var sx = (x + 0.5) * horizontal;
                var sy = (y + 0.5) * vertical;
                if (nearest)
                    pixels.AsSpan(((int)sy * width + (int)sx) * 4, 4).CopyTo(destination);
                else
                    Sample(pixels, width, height, sx, sy, horizontal, vertical, destination);
            }
        }
        return (output, targetWidth, targetHeight);
    }

    private static void Sample(byte[] pixels, int width, int height, double sx, double sy, double rx, double ry,
        Span<byte> destination)
    {
        var total = 0d;
        var alpha = 0d;
        Span<double> color = stackalloc double[3];
        color.Clear();
        for (var y = Math.Max(0, (int)(sy - ry + 0.5)); y < Math.Min(height, sy + ry - 0.5); y++)
        for (var x = Math.Max(0, (int)(sx - rx + 0.5)); x < Math.Min(width, sx + rx - 0.5); x++)
        {
            var weight = (1 - Math.Abs(sx - x - 0.5) / rx) * (1 - Math.Abs(sy - y - 0.5) / ry);
            var offset = (y * width + x) * 4;
            var weightedAlpha = pixels[offset + 3] * weight;
            total += weight;
            alpha += weightedAlpha;
            for (var c = 0; c < 3; c++)
                color[c] += pixels[offset + c] * weightedAlpha;
        }
        destination[3] = (byte)Math.Round(alpha / total);
        if (alpha > 0)
            for (var c = 0; c < 3; c++)
                destination[c] = (byte)Math.Round(color[c] / alpha);
    }

    private static byte[] WritePng(byte[] pixels, int width, int height, CancellationToken token)
    {
        var stride = width * 4;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[stride + 1];
            row[0] = 1; // PNG Sub filter.
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                var source = pixels.AsSpan(y * stride, stride);
                source[..4].CopyTo(row.AsSpan(1));
                for (var x = 4; x < stride; x++)
                    row[x + 1] = (byte)(source[x] - source[x - 4]);
                zlib.Write(row);
            }
        }
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> name, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        stream.Write(name);
        stream.Write(data);
        var crc = UpdateCrc(UpdateCrc(uint.MaxValue, name), data);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }
}
