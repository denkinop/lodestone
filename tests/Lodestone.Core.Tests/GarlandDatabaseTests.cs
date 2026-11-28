using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Lodestone.Core.Assets;
using Lodestone.Core.Database;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class GarlandDatabaseTests
{
    [Fact]
    public async Task SearchesSimplifiedChineseAndKeepsIdentifiersAndCategoriesInMixedResults()
    {
        using var client = new GarlandDatabaseClient(new HttpClient(new Fixture()));
        var rows = await client.SearchAsync("仙人掌 & 水晶", "", CancellationToken.None);

        Assert.Collection(rows,
            mob => Assert.Equal(new GarlandEntry("mob", "1750000000287", "仙人掌", 0, "等级 3 - 4"), mob),
            item => Assert.Equal(new GarlandEntry("item", "13114", "无瑕白染剂", 22820, "品级 1"), item));
    }

    [Theory]
    [InlineData("item")]
    [InlineData("")]
    public async Task FindsItemNameSuffixesMissingFromGarlandPrefixResults(string type)
    {
        using var client = new GarlandDatabaseClient(new HttpClient(new Fixture()));
        var results = await client.SearchAsync("原木", type, CancellationToken.None);
        var spruce = Assert.Single(results, entry => entry is { Type: "item", Id: "5395" });
        Assert.Equal(new GarlandEntry("item", "5395", "云杉原木", 22413, "品级 55"), spruce);
        Assert.Contains(results, entry => entry is { Type: "item", Name: "原木立柱" });
        Assert.Contains(results, entry => entry is { Type: "item", Name: "试验原木来源" });
        if (type.Length == 0)
            Assert.Contains(results, entry => entry is { Type: "quest", Name: "运送原木" });
    }

    [Fact]
    public async Task MergesItemResultsByIdAndIncludesUntradableItems()
    {
        using var client = new GarlandDatabaseClient(new HttpClient(new Fixture()));
        var token = CancellationToken.None;
        var spruce = await client.SearchAsync("云杉原木", "item", token);
        Assert.Equal("5395", spruce[0].Id);
        Assert.Single(spruce, entry => entry.Id == "5395");
        var gear = await client.SearchAsync("秘银双手剑", "item", token);
        Assert.Contains(gear, entry => entry.Name == "以太秘银双手剑");
    }

    [Theory]
    [InlineData("item")]
    [InlineData("")]
    public async Task ReturnsLocalItemsWhenRemoteSearchFails(string type)
    {
        using var client = new GarlandDatabaseClient(new HttpClient(new Fixture(HttpStatusCode.ServiceUnavailable)));
        var (entries, localOnly) = await client.SearchWithStatusAsync("以太秘银双手剑", type, CancellationToken.None);
        Assert.True(localOnly);
        Assert.Equal(new GarlandEntry("item", "10442", "以太秘银双手剑", 34256, "品级 41"), Assert.Single(entries));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync("以太秘银双手剑", "quest", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync("no-such-local-item", type, CancellationToken.None));
    }

    [Fact]
    public async Task SearchesKnownLocalItemIdsWithoutDownloadingDetails()
    {
        var handler = new Fixture();
        using var client = new GarlandDatabaseClient(new HttpClient(handler));
        var entry = Assert.Single(await client.SearchAsync("＃１０４４２", "item", CancellationToken.None));
        Assert.Equal("以太秘银双手剑", entry.Name);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task ReusesDetailFromNumericSearchAndRefreshesAfterExpiration()
    {
        var handler = new Fixture();
        var clock = new TestClock();
        using var client = new GarlandDatabaseClient(new HttpClient(handler), clock: clock);
        var entry = Assert.Single(await client.SearchAsync("66214", "quest", CancellationToken.None));
        Assert.Equal(entry, (await client.GetDetailAsync(entry.Type, entry.Id, CancellationToken.None))?.Entry);
        Assert.NotNull(await client.GetDetailAsync(entry.Type, entry.Id, CancellationToken.None));
        Assert.Single(handler.Paths);

        clock.Now = clock.Now.AddMinutes(6);
        Assert.NotNull(await client.GetDetailAsync(entry.Type, entry.Id, CancellationToken.None));
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task LinkedItemsUseTheCurrentFullCatalogAndInvalidateCachedDetails()
    {
        var handler = new Fixture();
        var current = ExdCatalog.Bundled;
        using var client = new GarlandDatabaseClient(new HttpClient(handler), () => current);
        const string id = "1021";
        var detail = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("achievement", id, CancellationToken.None));
        Assert.Equal(new GarlandEntry("item", "10442", "以太秘银双手剑", 34256, null),
            Assert.Single(Assert.Single(detail.Links).Entries));

        using var bundled = typeof(ExdCatalog).Assembly.GetManifestResourceStream("Lodestone.Data.Exd")!;
        using var gzip = new GZipStream(bundled, CompressionMode.Decompress);
        var json = JsonNode.Parse(gzip)!;
        json["databaseItems"] = JsonNode.Parse("""[[10442,"更新后的双手剑",34257,42]]""");
        using var snapshot = new MemoryStream();
        using (var compressed = new GZipStream(snapshot, CompressionLevel.Fastest, leaveOpen: true))
            System.Text.Json.JsonSerializer.Serialize(compressed, json);
        snapshot.Position = 0;
        current = ExdCatalog.Read(snapshot, ExdCatalog.Bundled);

        var updated = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("achievement", id, CancellationToken.None));
        Assert.Equal(new GarlandEntry("item", "10442", "更新后的双手剑", 34257, null),
            Assert.Single(Assert.Single(updated.Links).Entries));
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task ReadsVersionedDocumentsAndResolvesQuestLinksAndAchievementRewards()
    {
        using var client = new GarlandDatabaseClient(new HttpClient(new Fixture()));
        var token = CancellationToken.None;

        var result = Assert.Single(await client.SearchAsync("＃６６２１４", "quest", token));
        Assert.Equal("沙之都的工作", result.Name);
        var quest = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("quest", result.Id, token));
        Assert.Contains(new GarlandField("任务等级", "16"), quest.Fields);
        var issuer = Assert.Single(Assert.Single(quest.Links, link => link.Label == "接取 NPC").Entries);
        Assert.Equal("缪恩", issuer.Name);
        Assert.Equal("1000100", issuer.Id);

        var item = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("item", "13114", token));
        Assert.Contains(new GarlandField("堆叠上限", "999"), item.Fields);
        Assert.Equal("第一行\n第二行", item.Description);
        var achievement = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("achievement", "1020", token));
        Assert.Contains(new GarlandField("成就点数", "5"), achievement.Fields);
        Assert.Equal("羽饰装甲", Assert.Single(Assert.Single(achievement.Links).Entries).Name);
        Assert.Null(await client.GetDetailAsync("quest", "../item", token));
        Assert.Null(await client.GetDetailAsync("achievement", "999", token));
    }

    [Fact]
    public async Task UsesBundledNamesAndIconsWhenRecipeContainsOnlyCrystalIds()
    {
        using var client = new GarlandDatabaseClient(new HttpClient(new Fixture()));
        var detail = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("item", "10373", CancellationToken.None));
        var materials = Assert.Single(detail.Links);
        Assert.Equal("制作材料", materials.Label);
        Assert.Equal(new GarlandEntry("item", "10", "风之水晶", 20010, "数量 5"), materials.Entries[0]);
        Assert.Equal(new GarlandEntry("item", "9", "冰之水晶", 20009, "数量 5"), materials.Entries[1]);
    }

    [Fact]
    public async Task UsesMapSheetIdsForPinnedLocationsAndRegionOnlyEntries()
    {
        using var client = new GarlandDatabaseClient(new HttpClient(new Fixture()));
        var token = CancellationToken.None;
        var npc = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("npc", "1039940", token));
        Assert.Equal(new GarlandMap(697, "加雷马", 14.07m, 29.52m), npc.Map);
        Assert.Contains(new GarlandField("坐标", "X 14.07 · Y 29.52"), npc.Fields);

        var mob = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("mob", "24630000002310", token));
        Assert.Equal(new GarlandMap(5, "黑衣森林东部林区", null, null), mob.Map);
        var fishing = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("fishing", "125", token));
        Assert.Equal(new GarlandMap(213, "龙堡内陆低地", 23.17m, 23.55m), fishing.Map);
        var unknown = Assert.IsType<GarlandDetail>(await client.GetDetailAsync("npc", "22", token));
        Assert.Null(unknown.Map);
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture(HttpStatusCode searchStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.Equal("www.garlandtools.cn", uri.Host);
            Paths.Add(uri.AbsolutePath);
            if (uri.AbsolutePath == "/api/search.php" && searchStatus != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(searchStatus));
            var json = uri.AbsolutePath switch
            {
                "/api/search.php" => Search(uri.Query),
                "/db/doc/quest/chs/2/66214.json" => """
                    {"quest":{"id":66214,"name":"沙之都的工作","location":"格里达尼亚","issuer":1000100,
                      "reqs":{"jobs":[{"lvl":16}]},"reward":{"xp":7000}},
                     "partials":[{"type":"npc","id":"1000100","obj":{"n":"缪恩"}}]}
                    """,
                "/db/doc/item/chs/3/13114.json" => """
                    {"item":{"id":13114,"name":"无瑕白染剂","icon":22820,"description":"<b>第一行</b><br>第二行",
                      "ilvl":1,"tradeable":1,"stackSize":999}}
                    """,
                "/db/doc/item/chs/3/10373.json" => """
                    {"item":{"id":10373,"name":"魔导机械修理材料","craft":[{"ingredients":[{"id":10,"amount":5},{"id":9,"amount":5}]}]}}
                    """,
                "/db/doc/core/chs/3/data.json" => """
                    {"patch":{},"locationIndex":{"3710":{"name":"加雷马"},"55":{"name":"黑衣森林东部林区"},"2001":{"name":"龙堡内陆低地"}}}
                    """,
                "/db/doc/npc/chs/2/1039940.json" => """
                    {"npc":{"name":"露琪亚","id":1039940,"coords":[14.07,29.52],"zoneid":3710,"areaid":3903}}
                    """,
                "/db/doc/mob/chs/2/24630000002310.json" => """
                    {"mob":{"name":"第三大队先锋","id":24630000002310,"zoneid":55,"lvl":"42"}}
                    """,
                "/db/doc/fishing/chs/2/125.json" => """
                    {"fishing":{"name":"沙利亚克河","id":125,"x":23.17,"y":23.55,"zoneid":2001,"areaid":2039}}
                    """,
                "/db/doc/npc/chs/2/22.json" => """{"npc":{"name":"测试","id":22,"location":"无匹配测试地点"}}""",
                "/db/doc/achievement/chs/2/1020.json" => """
                    {"achievement":{"id":1020,"name":"陆行鸟训练师","points":5,"item":8571},
                     "partials":[{"type":"item","id":"8571","obj":{"n":"羽饰装甲","c":58579}}]}
                    """,
                "/db/doc/achievement/chs/2/1021.json" => """{"achievement":{"id":1021,"name":"测试成就","item":10442}}""",
                "/db/doc/achievement/chs/2/999.json" => null,
                _ => throw new InvalidOperationException("Unexpected request: " + uri.AbsolutePath)
            };
            return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }

        private static string Search(string query)
        {
            Assert.Contains("lang=chs", query);
            if (query.Contains("text=" + Uri.EscapeDataString("原木"), StringComparison.Ordinal))
                return """
                    [{"type":"item","id":"37352","obj":{"n":"原木立柱","l":1}},
                     {"type":"item","id":"99999999","obj":{"n":"试验原木来源","l":1}},
                     {"type":"quest","id":"123","obj":{"n":"运送原木"}}]
                    """;
            if (query.Contains("text=" + Uri.EscapeDataString("云杉原木"), StringComparison.Ordinal))
                return """[{"type":"item","id":"5395","obj":{"n":"云杉原木","l":55,"c":22413}}]""";
            if (query.Contains("text=" + Uri.EscapeDataString("秘银双手剑"), StringComparison.Ordinal))
                return "[]";
            Assert.Contains("text=" + Uri.EscapeDataString("仙人掌 & 水晶"), query);
            return """
                [{"type":"mob","id":"1750000000287","obj":{"n":"仙人掌","l":"3 - 4"}},
                 {"type":"item","id":"13114","obj":{"n":"无瑕白染剂","l":1,"c":22820}},
                 {"type":"item","id":"13114","obj":{"n":"无瑕白染剂","l":1,"c":22820}},
                 {"type":"item","id":"../1","obj":{"n":"无效"}},{"id":1}]
                """;
        }
    }
}
