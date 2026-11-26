using System.Buffers.Binary;
using System.IO.Compression;
using Lodestone.Core.Assets;
using Lodestone.Core.Lobby;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class CharacterAssetTests
{
    [Fact]
    public void MaterialDyesReplaceFlaggedValuesWithTheGameTemplate()
    {
        var shader = "character.shpk\0"u8;
        var dyeOffset = 16 + shader.Length + 2048;
        var data = new byte[dyeOffset + 128];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2176);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)shader.Length);
        shader.CopyTo(data.AsSpan(16));
        const uint template = 1100u << 16;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(dyeOffset), template | 1 | 16 | 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(dyeOffset + 4), template | (1u << 27) | 2);
        WriteHalf(data.AsSpan(16 + shader.Length), 0.25f);
        WriteHalf(data.AsSpan(16 + shader.Length + 2), 0.5f);

        var diffuse = new byte[12 + 254];
        WriteHalf(diffuse.AsSpan(0), 0.75f);
        WriteHalf(diffuse.AsSpan(2), 0.875f);
        WriteHalf(diffuse.AsSpan(4), 1f);
        WriteHalf(diffuse.AsSpan(6), 0.25f);
        diffuse[12] = 255;
        diffuse[13] = 1;
        diffuse[14] = 2;
        var specular = new byte[254 * 6];
        WriteHalf(specular.AsSpan(6), 0.125f);
        WriteHalf(specular.AsSpan(8), 0.625f);
        var metalness = new byte[2];
        WriteHalf(metalness, 0.25f);
        var roughness = new byte[2];
        WriteHalf(roughness, 0.625f);
        byte[][] columns = [diffuse, specular, [], [], metalness, roughness, [], [], [], [], [], []];
        var templateData = new byte[16 + columns.Length * 2 + columns.Sum(column => column.Length)];
        "MS"u8.CopyTo(templateData);
        BinaryPrimitives.WriteUInt16LittleEndian(templateData.AsSpan(2), 0x0201);
        BinaryPrimitives.WriteUInt16LittleEndian(templateData.AsSpan(4), 1);
        templateData[6] = 3;
        templateData[7] = 9;
        BinaryPrimitives.WriteUInt32LittleEndian(templateData.AsSpan(8), 1100);
        var payload = 16 + columns.Length * 2;
        var written = 0;
        for (var index = 0; index < columns.Length; index++)
        {
            columns[index].CopyTo(templateData, payload + written);
            written += columns[index].Length;
            BinaryPrimitives.WriteUInt16LittleEndian(templateData.AsSpan(16 + index * 2), (ushort)(written / 2));
        }

        var material = CharacterMaterial.Read(data);
        material.ApplyDyes(new StainingTemplate(templateData), 1, 2);

        Assert.Equal([0.75f, 0.875f, 1f], material.Palette[0][..3]);
        Assert.Equal(0.625f, material.Palette[0][3]);
        Assert.Equal(0.25f, material.Palette[0][7]);
        Assert.Equal(0, material.Palette[1][0]);
        Assert.Equal([0.125f, 0.625f, 0f], material.Palette[1][4..7]);
        Assert.Equal(0, material.Palette[2][0]);

        static void WriteHalf(Span<byte> destination, float value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(destination, BitConverter.HalfToUInt16Bits((Half)value));
    }

    [Fact]
    public void TexturePreservesBgraAndUsesRgbaInTheVirtualPng()
    {
        var data = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x1450);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 80);
        byte[] source = [5, 10, 15, 127, 2, 4, 7, 255];
        source.CopyTo(data, 80);
        var png = CharacterTexture.ToPng(data, false, CancellationToken.None);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal("IDAT"u8.ToArray(), png[37..41]);
        using var input = new MemoryStream(png, 41, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33)));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var row = new byte[9];
        zlib.ReadExactly(row);
        Assert.Equal(new byte[] { 1, 15, 10, 5, 127, 248, 250, 253, 128 }, row);
        Assert.Throws<SqPackException>(() => CharacterTexture.Decode(data[..^1], CancellationToken.None));
    }

    [Fact]
    public void MeshPartsHonorMaterialVariantsAndSeparateHelmetCoverage()
    {
        var mesh = new ModelMesh("", [], [0, 1, 2, 3, 4, 5, 6, 7, 8],
            [new(0, 3, 1), new(3, 3, 2), new(6, 3, 0)], [], null, null,
            new Dictionary<string, (ushort, ushort)[]>());
        var appearance = new CharacterAppearance(new int[26], 0, 0, []);
        var part = new CharacterScene.Part("hair", "", Mask: 0);
        var groups = CharacterScene.IndexGroups(mesh, ["atr_kam", "atr_tv_a"], part, appearance, 1ul << 41);
        Assert.Equal(new ushort[] { 6, 7, 8 }, groups[0].Indices);
        Assert.Equal(new ushort[] { 0, 1, 2 }, groups[1].Indices);
        Assert.True(groups[1].Hidden);
    }

    [Fact]
    public void LocalizedCandidatesIncludeTheSecondaryModelWithoutChangingGearIdentity()
    {
        var data = CharacterData.Instance;
        var matches = data.Candidates(1, new CharacterEquipment("off_hand", 351, 3, 1, 1, 2));
        var weapon = Assert.Single(matches, candidate => candidate is { Id: 1684, Name: "青铜指虎", Icon: 31013 });
        Assert.NotNull(weapon.Details);
        Assert.Equal("格斗武器", weapon.Details.Category);
        Assert.Equal(12, weapon.Details.ItemLevel);
        Assert.Equal(16, weapon.Details.PhysicalDamage);
        Assert.Contains(new EquipmentAttribute("力量", 2), weapon.Details.Attributes);
        var mainHand = data.Candidates(0, new CharacterEquipment("main_hand", 301, 3, 1, 0, 0));
        Assert.Equal("青铜指虎", mainHand[0].Name);
        Assert.Contains(mainHand, candidate => candidate.Name == "过期青铜指虎");
        Assert.Equal("素雪白", data.Stain(1)?.Name);
        Assert.Equal("苍白灰", data.Stain(2)?.Name);
        Assert.Null(data.Stain(0));
        Assert.Equal("红玉海", CharacterData.GetWorldName(1167));
        Assert.Null(CharacterData.GetWorldName(0));
        Assert.Equal("破坏神·拉尔戈", CharacterData.GetGuardianName(8));
        Assert.Null(CharacterData.GetGuardianName(0));
    }
}
