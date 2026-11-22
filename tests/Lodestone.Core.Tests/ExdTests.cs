using System.Net;
using Lodestone.Core.Assets;
using Lodestone.Core.Lobby;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class ExdTests
{
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
        public bool ChangeVersionDuringWeather { get; set; }

        private const string Item = """
            {"Name":"测试装备","Icon@as(raw)":12,"ModelMain":1,"ModelSub":2,"EquipSlotCategory@as(raw)":1,
             "ItemUICategory@as(raw)":1,"ClassJobCategory@as(raw)":1,"Rarity":1,"LevelItem@as(raw)":3,"LevelEquip":2,
             "DamagePhys":4,"DamageMag":5,"DefensePhys":0,"DefenseMag":0,"BaseParam@as(raw)":[1,null],"BaseParamValue":[2,0]}
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.Equal("xivapi-v2.xivcdn.com", uri.Host);
            if (uri.AbsolutePath == "/api/asset")
            {
                Assert.Equal("image/webp", Assert.Single(request.Headers.Accept).MediaType);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ExdCatalog.Bundled.Weather.Weather[1].Icon)
                });
            }
            var table = uri.Segments[^1];
            Assert.Contains("language=chs", uri.Query);
            var nextPage = uri.Query.Contains("&after=", StringComparison.Ordinal);
            if (table != "EquipSlotCategory" || nextPage)
                Assert.Contains("&schema=fixture-schema", uri.Query);

            var rows = !nextPage ? Row(1, Fields[table])
                : table == "Item" && uri.Query.EndsWith("&after=1", StringComparison.Ordinal)
                    ? Row(2, Item.Replace("测试装备", "测试装备 II", StringComparison.Ordinal))
                    : "[]";
            var version = ChangeVersionDuringWeather && table == "Weather" ? "fixture-2" : "fixture-1";
            var json = $$"""{"version":"{{version}}","schema":"fixture-schema","rows":{{rows}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }

        private static string Row(int id, string fields) => $$"""[{"row_id":{{id}},"fields":{{fields}}}]""";
    }
}
