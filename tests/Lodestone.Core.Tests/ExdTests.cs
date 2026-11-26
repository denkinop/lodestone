using System.Net;
using Lodestone.Core.Assets;
using Lodestone.Core.Lobby;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class ExdTests
{
    [Fact]
    public void WardrobeSearchUsesItemSlotsAndKeepsRingChoicesIndependent()
    {
        var wardrobe = ExdCatalog.Bundled.Character.Wardrobe;
        var page = wardrobe.Search("body", "", 0);
        Assert.True(page.More);
        Assert.NotEmpty(page.Items);
        Assert.Empty(page.Items.Select(item => item.Id).Intersect(wardrobe.Search("body", "", 1).Items.Select(item => item.Id)));

        var robe = Assert.Single(wardrobe.Search("body", "朱红术士斗篷", 0).Items);
        Assert.Equal(24855, robe.Id);
        Assert.Equal("head", Assert.Single(robe.Covers));
        Assert.Empty(wardrobe.Search("head", "24855", 0).Items);
        Assert.Empty(wardrobe.Search("main_hand", "1601", 0).Items);
        Assert.Empty(wardrobe.Search("off_hand", "1684", 0).Items);
        var ring = Assert.Single(wardrobe.Search("left_ring", "4299", 0).Items);
        Assert.Equal(ring.Id, Assert.Single(wardrobe.Search("right_ring", "4299", 0).Items).Id);
        Assert.Empty(ring.Covers);
    }

    [Fact]
    public void WardrobeRestoresAnEarlierChoiceWhenTheCoveringItemIsRemoved()
    {
        var wardrobe = ExdCatalog.Bundled.Character.Wardrobe;
        var weapon = new CharacterEquipment("main_hand", 301, 3, 1, 1, 2);
        var offHand = new CharacterEquipment("off_hand", 351, 3, 1, 1, 2);
        var originalBody = new CharacterEquipment("body", 6, 0, 1, 0, 0);
        CharacterEquipment[] original = [weapon, offHand, originalBody];
        WardrobeChange[] changes = [new("head", 2629), new("left_ring", 4299), new("right_ring", 4299), new("body", 24855)];

        var covered = wardrobe.Compose(original, changes);
        Assert.Equal("body", covered.CoveredSlots["head"]);
        Assert.DoesNotContain(covered.Equipment, item => item.Slot == "head");
        Assert.DoesNotContain(covered.Equipment, item => item.Slot is "main_hand" or "off_hand");

        var restored = wardrobe.Compose(original, changes[..^1]);
        Assert.Empty(restored.CoveredSlots);
        Assert.Contains(originalBody, restored.Equipment);
        Assert.Contains(restored.Equipment, item => item is { Slot: "head", Model: 4, Variant: 1 });
        Assert.Contains(restored.Equipment, item => item.Slot == "left_ring");
        Assert.Contains(restored.Equipment, item => item.Slot == "right_ring");

        var editedCoveredSlot = wardrobe.Compose(original, [new("body", 24855), new("head", 2629)]);
        Assert.Empty(editedCoveredSlot.CoveredSlots);
        Assert.Contains(originalBody, editedCoveredSlot.Equipment);
        Assert.DoesNotContain(wardrobe.Compose(original, [new("body", 0)]).Equipment, item => item.Slot == "body");
        Assert.Throws<SqPackException>(() => wardrobe.Compose(original, [new("off_hand", 1684)]));
    }

    [Fact]
    public void WardrobeDyesOriginalAndTriedGearWithSeparateChannels()
    {
        var wardrobe = ExdCatalog.Bundled.Character.Wardrobe;
        Assert.Contains(wardrobe.Dyes, dye => dye is { Id: 1, Name: "素雪白" });
        Assert.DoesNotContain(wardrobe.Dyes, dye => dye.Id == 126);
        var original = new CharacterEquipment("body", 53, 0, 1, 1, 8);

        var dyedOriginal = wardrobe.Compose([original], [new("body", Stain1: 0)]);
        var baseGear = Assert.Single(dyedOriginal.Equipment);
        Assert.Equal(original with { Stain1 = 0 }, baseGear);
        Assert.Equal(2, dyedOriginal.DyeChannels["body"]);
        Assert.Empty(dyedOriginal.TriedSlots);
        Assert.Equal(original, Assert.Single(wardrobe.Compose([original], []).Equipment));

        var dyedTrial = wardrobe.Compose([original], [new("body", 2965, 1, 7)]);
        Assert.Equal(original with { Stain2 = 7 }, Assert.Single(dyedTrial.Equipment));
        Assert.Equal("body", Assert.Single(dyedTrial.TriedSlots));
        Assert.Equal(2, dyedTrial.DyeChannels["body"]);

        Assert.Throws<SqPackException>(() => wardrobe.Compose([], [new("head", 2629, Stain2: 1)]));
        Assert.Throws<SqPackException>(() => wardrobe.Compose([], [new("body", 11111, Stain1: 1)]));
        Assert.Throws<SqPackException>(() => wardrobe.Compose([], [new("body", 2965, Stain1: 255)]));
        Assert.Throws<SqPackException>(() => wardrobe.Compose([], [new("body", Stain1: 1)]));
    }

    [Fact]
    public async Task PublishesBothCatalogsTogetherAndKeepsThePreviousSnapshotOnVersionMismatch()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "zh-cn.json.gz");
            await File.WriteAllTextAsync(path, "damaged snapshot");
            var api = new XivApiFixture();
            using var http = new HttpClient(api);
            var store = new ExdDataStore(path, http);
            Assert.Same(ExdCatalog.Bundled, store.Current);

            var updated = await store.RefreshAsync();
            Assert.Same(updated, store.Current);
            var reloaded = new ExdDataStore(path, http).Current;
            Assert.Equal("fixture-1", reloaded.Version);
            Assert.Equal("测试服务器", reloaded.Character.WorldName(1));
            Assert.Equal("#123456", reloaded.Character.StainColor(1));
            var matches = reloaded.Character.Candidates(1, new CharacterEquipment("off_hand", 2, 0, 0, 0, 0));
            Assert.Collection(matches, first => Assert.Equal("测试装备", first.Name), second => Assert.Equal("测试装备 II", second.Name));
            var map = Assert.Single(Assert.Single(reloaded.Weather.Regions).Maps);
            Assert.Equal("测试地图", map.Name);
            Assert.Equal(new WeatherRate(1, 100), Assert.Single(map.Rates));
            Assert.Equal("测试天气", reloaded.Weather.Weather[1].Name);
            Assert.True(ExdCatalog.IsWebp(reloaded.Weather.Weather[1].Icon));

            var saved = await File.ReadAllBytesAsync(path);
            api.ChangeVersionDuringWeather = true;
            await Assert.ThrowsAsync<InvalidDataException>(() => store.RefreshAsync());
            Assert.Same(updated, store.Current);
            Assert.Equal(saved, await File.ReadAllBytesAsync(path));
            Assert.Single(directory.GetFiles());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class XivApiFixture : HttpMessageHandler
    {
        private readonly TaskCompletionSource itemStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource categoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ChangeVersionDuringWeather { get; set; }

        private const string Item = """
            {"Name":"测试装备","Icon@as(raw)":12,"ModelMain":1,"ModelSub":2,"EquipSlotCategory@as(raw)":1,
             "ItemUICategory@as(raw)":1,"ClassJobCategory@as(raw)":1,"Rarity":1,"LevelItem@as(raw)":3,"LevelEquip":2,
             "DamagePhys":4,"DamageMag":5,"DefensePhys":0,"DefenseMag":0,"DyeCount":1,"BaseParam@as(raw)":[1,null],"BaseParamValue":[2,0]}
            """;

        private static readonly Dictionary<string, string> Fields = new()
        {
            ["EquipSlotCategory"] = """
                {"MainHand":1,"OffHand":-1,"Head":0,"Body":0,"Gloves":0,"Legs":0,"Feet":0,
                 "Ears":0,"Neck":0,"Wrists":0,"FingerL":0,"FingerR":0}
                """,
            ["ItemUICategory"] = """{"Name":"测试分类"}""",
            ["ClassJobCategory"] = """{"Name":"测试职业"}""",
            ["BaseParam"] = """{"Name":"测试属性"}""",
            ["Stain"] = """{"Name":"测试染色","Color":4279383126}""",
            ["World"] = """{"Name":"测试服务器","IsPublic":false,"DataCenter":{"fields":{"Region@as(raw)":5}}}""",
            ["GuardianDeity"] = """{"Name":"测试守护神"}""",
            ["Item"] = Item,
            ["TerritoryType"] = """
                {"Name":"test","Map@as(raw)":1,"PlaceName":{"fields":{"Name":"测试地图"}},
                 "PlaceNameRegion":{"value":1,"fields":{"Name":"测试地区"}},"TerritoryIntendedUse@as(raw)":1,"WeatherRate@as(raw)":1}
                """,
            ["WeatherRate"] = """{"Weather@as(raw)":[1,0],"Rate":[90,0]}""",
            ["Weather"] = """{"Name":"测试天气","Icon":{"path_hr1":"ui/icon/060000/060201_hr1.tex"}}"""
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.Equal("xivapi-v2.xivcdn.com", uri.Host);
            if (uri.AbsolutePath == "/api/asset")
            {
                Assert.Equal("image/webp", Assert.Single(request.Headers.Accept).MediaType);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ExdCatalog.Bundled.Weather.Weather[1].Icon)
                };
            }
            var table = uri.Segments[^1];
            Assert.Contains("language=chs", uri.Query);
            var nextPage = uri.Query.Contains("&after=", StringComparison.Ordinal);
            if (table != "EquipSlotCategory" || nextPage)
                Assert.Contains("&schema=fixture-schema", uri.Query);
            if (!nextPage && table == "Item")
            {
                itemStarted.TrySetResult();
                await categoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            if (!nextPage && table == "ItemUICategory")
            {
                categoryStarted.TrySetResult();
                await itemStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            var rows = !nextPage ? Row(1, Fields[table])
                : table == "Item" && uri.Query.EndsWith("&after=1", StringComparison.Ordinal)
                    ? Row(2, Item.Replace("测试装备", "测试装备 II", StringComparison.Ordinal))
                    : "[]";
            var version = ChangeVersionDuringWeather && table == "Weather" ? "fixture-2" : "fixture-1";
            var json = $$"""{"version":"{{version}}","schema":"fixture-schema","rows":{{rows}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }

        private static string Row(int id, string fields) => $$"""[{"row_id":{{id}},"fields":{{fields}}}]""";
    }
}
