using System.Net;
using Lodestone.Core.Assets;
using Lodestone.Core.Market;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class MarketTests
{
    [Fact]
    public void BundledMarketSearchFindsMaterialsAndDyesByChineseNameOrId()
    {
        var market = ExdCatalog.Bundled.MarketItems;
        var dye = Assert.Single(market.Search("无瑕白染剂"));
        Assert.Equal(13114, dye.Id);
        Assert.Equal(dye, Assert.Single(market.Search("＃１３，１１４")));
        Assert.Equal("火之碎晶", Assert.Single(market.Search("2")).Name);
        var magitek = market.Search("魔导");
        Assert.NotEmpty(magitek);
        Assert.All(magitek, item => Assert.Contains("魔导", item.Name));
        Assert.Contains(market.Search("幻梦武器"), item => item.Id == 45070);
        Assert.Empty(market.Search("以太秘银双手剑"));
    }

    [Fact]
    public async Task JoinsTheDirectoryAndReadsPricesForSelectedWorldsOrWholeDataCenters()
    {
        using var http = new HttpClient(new MarketFixture());
        using var market = new UniversalisClient(http);
        var token = CancellationToken.None;
        var local = Assert.IsType<MarketBoard>(await market.GetBoardAsync(1042, null, 13114, token));
        Assert.True(local.HasData);
        Assert.Equal(400, local.Listings[0].PricePerUnit);
        Assert.Equal(1795819290822, local.LastUploadTime);
        Assert.Equal(1795813384, Assert.Single(local.RecentHistory).Timestamp);

        var centers = await market.GetDataCentersAsync(token);
        var current = Assert.Single(centers, center => center.Worlds.Any(world => world.Id == 1042));
        Assert.Equal("陆行鸟", current.Name);
        Assert.Equal("中国", current.Region);
        Assert.Equal(new MarketWorld(1044, "幻影群岛"), current.Worlds[1]);
        Assert.Same(centers, await market.GetDataCentersAsync(token));
        var other = Assert.Single(centers, center => center.Name == "莫古力");
        Assert.NotNull(await market.GetBoardAsync(Assert.Single(other.Worlds).Id, null, 13114, token));
        var across = Assert.IsType<MarketBoard>(await market.GetBoardAsync(0, current.Name, 13114, token));
        Assert.Equal("拉诺西亚", across.Listings[0].World);
        var update = Assert.Single(await market.GetRecentAsync(0, current.Name, token));
        Assert.Equal(1795819290822, update.LastUploadTime);
        Assert.Null(await market.GetBoardAsync(1042, null, 999999, token));
    }

    private sealed class MarketFixture : HttpMessageHandler
    {
        private int directoryReads;
        private int worldReads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.Equal("universalis.app", uri.Host);
            var path = Uri.UnescapeDataString(uri.PathAndQuery);
            if (uri.AbsolutePath == "/api/v2/1042/999999")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            string json;
            if (path == "/api/v2/data-centers")
            {
                Assert.Equal(1, ++directoryReads);
                json = """[{"name":"莫古力","region":"中国","worlds":[1172]},{"name":"陆行鸟","region":"中国","worlds":[1042,1044,9999]}]""";
            }
            else if (path == "/api/v2/worlds")
            {
                Assert.Equal(1, ++worldReads);
                json = """[{"id":1044,"name":"幻影群岛"},{"id":1172,"name":"白银乡"},{"id":1042,"name":"拉诺西亚"}]""";
            }
            else if (path == "/api/v2/extra/stats/most-recently-updated?dcName=陆行鸟&entries=24")
            {
                json = """
                    {"items":[{"itemID":13114,"lastUploadTime":1795819290000,"worldName":"幻影群岛"},
                              {"itemID":13114,"lastUploadTime":1795819290822,"worldName":"拉诺西亚"}]}
                    """;
            }
            else
            {
                Assert.True(path.StartsWith("/api/v2/1042/13114?", StringComparison.Ordinal)
                    || path.StartsWith("/api/v2/1172/13114?", StringComparison.Ordinal)
                    || path.StartsWith("/api/v2/陆行鸟/13114?", StringComparison.Ordinal));
                Assert.Contains("listings=25&entries=15", path);
                json = """
                    {"itemID":13114,"lastUploadTime":1795819290822,"hasData":true,
                     "listings":[{"pricePerUnit":500,"quantity":6,"hq":true,"worldName":"幻影群岛"},
                                 {"pricePerUnit":400,"quantity":7,"hq":false,"worldName":"拉诺西亚"}],
                     "recentHistory":[{"pricePerUnit":350,"quantity":1,"hq":false,"timestamp":1795813384}]}
                    """;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
