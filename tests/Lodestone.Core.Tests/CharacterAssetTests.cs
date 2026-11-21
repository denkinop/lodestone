using System.Buffers.Binary;
using System.IO.Compression;
using Lodestone.Core.Assets;
using Lodestone.Core.Lobby;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class CharacterAssetTests
{
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
