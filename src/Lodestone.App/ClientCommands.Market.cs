using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lodestone.Core.Assets;
using Lodestone.Core.Market;

namespace Lodestone.App;

internal sealed partial class ClientCommands
{
    private readonly UniversalisClient market = new();

    public Task<MarketItem[]> SearchMarketItems(string encodedQuery) => Task.Run(() =>
        Exd.Current.MarketItems.Search(Uri.UnescapeDataString(encodedQuery)));

    public Task<MarketResponse<MarketDataCenter[]>> GetMarketDataCenters() =>
        ReadMarketAsync<MarketDataCenter[]>(async token => await market.GetDataCentersAsync(token).ConfigureAwait(false));

    public Task<MarketResponse<MarketRecentItem[]>> GetRecentMarketItems(int worldId, string? encodedDataCenter) =>
        ReadMarketScopeAsync<MarketRecentItem[]>(worldId, encodedDataCenter, async (dataCenter, token) =>
        {
            var updates = await market.GetRecentAsync(worldId, dataCenter, token).ConfigureAwait(false);
            var items = Exd.Current.MarketItems;
            return updates.Select(update => new MarketRecentItem(items.Get(update.ItemId), update.LastUploadTime, update.World)).ToArray();
        });

    public Task<MarketResponse<MarketBoard>> GetMarketPrices(int worldId, string? encodedDataCenter, int itemId) => itemId <= 0
        ? Task.FromResult(new MarketResponse<MarketBoard>(Error: "物品 ID 无效"))
        : ReadMarketScopeAsync(worldId, encodedDataCenter, (dataCenter, token) => market.GetBoardAsync(worldId, dataCenter, itemId, token));

    private Task<MarketResponse<T>> ReadMarketScopeAsync<T>(int worldId, string? encodedDataCenter,
        Func<string?, CancellationToken, Task<T?>> read) where T : class
    {
        var dataCenter = encodedDataCenter is null ? null : Uri.UnescapeDataString(encodedDataCenter);
        if (dataCenter is null ? worldId is <= 0 or > ushort.MaxValue : worldId != 0)
            return Task.FromResult(new MarketResponse<T>(Error: "请选择有效的数据中心或服务器"));
        return ReadMarketAsync<T>(async token =>
        {
            if (dataCenter is not null && !(await market.GetDataCentersAsync(token).ConfigureAwait(false))
                .Any(center => center.Name == dataCenter))
                throw new InvalidDataException("Universalis 尚未收录所选的数据中心");
            return await read(dataCenter, token).ConfigureAwait(false);
        });
    }

    private async Task<MarketResponse<T>> ReadMarketAsync<T>(Func<CancellationToken, Task<T?>> read) where T : class
    {
        CancellationToken token;
        lock (gate)
        {
            if (disposed)
                return new(Error: "窗口已关闭");
            token = lifetime.Token;
        }
        try
        {
            return new(await read(token).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            return new(Error: ex.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "查询过于频繁，请稍后重试",
                HttpStatusCode.NotFound => "Universalis 尚未收录当前市场",
                _ => "无法连接 Universalis，请稍后重试",
            });
        }
        catch (OperationCanceledException) { return new(Error: "市价查询超时，请重试"); }
        catch (InvalidDataException ex) { return new(Error: ex.Message); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new(Error: "无法读取 Universalis 返回的数据，请稍后重试");
        }
    }
}

internal sealed record MarketRecentItem(
    [property: JsonPropertyName("item")] MarketItem Item,
    [property: JsonPropertyName("lastUploadTime")] long LastUploadTime,
    [property: JsonPropertyName("world")] string? World);

internal sealed record MarketResponse<T>(
    [property: JsonPropertyName("data")] T? Data = null,
    [property: JsonPropertyName("error")] string? Error = null) where T : class;
