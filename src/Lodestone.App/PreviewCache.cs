using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Lodestone.Core.Assets;

namespace Lodestone.App;

internal static class PreviewCache
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxPngBytes = 4 << 20;
    private const string PngDataPrefix = "data:image/png;base64,";
    internal const int MaxPngDataLength = 5_600_000;

    internal static byte[]? DecodePng(string data)
    {
        if (data.Length > MaxPngDataLength || !data.StartsWith(PngDataPrefix, StringComparison.Ordinal))
            return null;
        try
        {
            var png = Convert.FromBase64String(data[PngDataPrefix.Length..]);
            return IsPng(png) ? png : null;
        }
        catch (FormatException) { return null; }
    }

    internal static SceneAssets? ReadScene(PreviewCharacter character)
    {
        try
        {
            using var input = ZipFile.OpenRead(Path.Combine(character.Directory, "scene-v1.zip"));
            if (!ReadEntry(input, "revision", 16).AsSpan().SequenceEqual(character.Revision.ToByteArray()))
                return null;
            var scene = JsonSerializer.Deserialize<SceneData>(ReadEntry(input, "scene.json", 72 << 20), Json);
            if (scene is null)
                return null;
            var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var id in scene.Materials.Values.SelectMany(material => material.Maps.Values).Distinct())
            {
                if (id.Length != 32 || !id.All(Uri.IsHexDigit))
                    return null;
                var png = ReadEntry(input, $"texture/{id}.png", 8 << 20);
                total += png.Length;
                if (total > 64 << 20)
                    return null;
                textures[id] = png;
            }
            return new(scene, textures);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    internal static void SaveScene(PreviewCharacter character, SceneAssets scene) =>
        CharacterCache.Write(Path.Combine(character.Directory, "scene-v1.zip"), output =>
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            WriteEntry(zip, "revision", character.Revision.ToByteArray());
            using (var json = zip.CreateEntry("scene.json", CompressionLevel.Fastest).Open())
                JsonSerializer.Serialize(json, scene.Scene, Json);
            foreach (var (id, png) in scene.Textures)
                WriteEntry(zip, $"texture/{id}.png", png);
        });

    internal static byte[]? ReadPng(PreviewCharacter character, string file)
    {
        try
        {
            using var input = File.OpenRead(Path.Combine(character.Directory, PngCacheName(file)));
            if (input.Length is < 40 or > MaxPngBytes + 16)
                return null;
            Span<byte> revision = stackalloc byte[16];
            input.ReadExactly(revision);
            if (new Guid(revision) != character.Revision)
                return null;
            var png = new byte[input.Length - 16];
            input.ReadExactly(png);
            return IsPng(png) ? png : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void SavePng(PreviewCharacter character, string file, byte[] png) =>
        CharacterCache.Write(Path.Combine(character.Directory, PngCacheName(file)), output =>
        {
            output.Write(character.Revision.ToByteArray());
            output.Write(png);
        });

    private static string PngCacheName(string file) => file switch
    {
        "full.png" => "full-v2.png",
        "portrait.png" => "portrait-v3.png",
        _ => file
    };

    internal static bool IsPng(ReadOnlySpan<byte> png) => png.Length is >= 33 and <= MaxPngBytes
        && png.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && png[12..16].SequenceEqual("IHDR"u8)
        && BinaryPrimitives.ReadUInt32BigEndian(png[16..]) is > 0 and <= 2048
        && BinaryPrimitives.ReadUInt32BigEndian(png[20..]) is > 0 and <= 2048;

    private static byte[] ReadEntry(ZipArchive zip, string name, int max)
    {
        if (zip.GetEntry(name) is not { Length: > 0 } entry || entry.Length > max)
            throw new InvalidDataException("角色资源缓存不完整");
        var data = new byte[entry.Length];
        using var input = entry.Open();
        input.ReadExactly(data);
        return data;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        entry.Write(data);
    }
}
