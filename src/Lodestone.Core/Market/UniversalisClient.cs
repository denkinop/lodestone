using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Lodestone.Core.Market;

public sealed record MarketPrice(
    [property: JsonPropertyName("pricePerUnit")] long PricePerUnit,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("hq")] bool HighQuality,
    [property: JsonPropertyName("worldName")] string? World,
    [property: JsonPropertyName("timestamp")] long? Timestamp);

public sealed record MarketBoard(
    [property: JsonPropertyName("itemID")] int ItemId,
    [property: JsonPropertyName("lastUploadTime")] long LastUploadTime,
    [property: JsonPropertyName("hasData")] bool HasData,
    [property: JsonPropertyName("listings")] MarketPrice[] Listings,
    [property: JsonPropertyName("recentHistory")] MarketPrice[] RecentHistory);

public sealed record MarketUpdate(
    [property: JsonPropertyName("itemID")] int ItemId,
    [property: JsonPropertyName("lastUploadTime")] long LastUploadTime,
    [property: JsonPropertyName("worldName")] string? World);

public sealed record MarketWorld(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record MarketDataCenter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("worlds")] MarketWorld[] Worlds);

public sealed class UniversalisClient : IDisposable
{
    private const string Api = "https://universalis.app/api/v2/";
    private const string BoardFields =
        "itemID,lastUploadTime,hasData,listings.pricePerUnit,listings.quantity,listings.hq,listings.worldName," +
        "recentHistory.pricePerUnit,recentHistory.quantity,recentHistory.hq,recentHistory.worldName,recentHistory.timestamp";
    private readonly HttpClient http;
    private DirectorySnapshot? directory;

    public UniversalisClient() : this(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(12)
    }) { }

    internal UniversalisClient(HttpClient http) => this.http = http;

    public async Task<MarketDataCenter[]> GetDataCentersAsync(CancellationToken token)
    {
        var cached = Volatile.Read(ref directory);
        if (cached is not null && cached.Expires > DateTimeOffset.UtcNow)
            return cached.DataCenters;

        var centersTask = ReadAsync<DataCenter[]>("data-centers", token);
        var worldsTask = ReadAsync<MarketWorld[]>("worlds", token);
        await Task.WhenAll(centersTask, worldsTask).ConfigureAwait(false);
        var worlds = (await worldsTask.ConfigureAwait(false))
            .Where(world => world is { Id: > 0 } && !string.IsNullOrWhiteSpace(world.Name))
            .DistinctBy(world => world.Id).ToDictionary(world => world.Id);
        var dataCenters = (await centersTask.ConfigureAwait(false))
            .Where(center => center is { Worlds.Length: > 0 } && !string.IsNullOrWhiteSpace(center.Name))
            .Select(center => new MarketDataCenter(center.Name, center.Region,
                center.Worlds.Distinct().Select(id => worlds.GetValueOrDefault(id)).OfType<MarketWorld>().ToArray()))
            .Where(center => center.Worlds.Length > 0).ToArray();
        if (dataCenters.Length == 0)
            throw new InvalidDataException("Universalis 返回的数据中心和服务器列表为空");
        Volatile.Write(ref directory, new(dataCenters, DateTimeOffset.UtcNow.AddHours(1)));
        return dataCenters;
    }

    public async Task<MarketUpdate[]> GetRecentAsync(int worldId, string? dataCenter, CancellationToken token)
    {
        var scope = dataCenter is null ? "world=" + Id(worldId) : "dcName=" + Uri.EscapeDataString(dataCenter);
        var recent = await ReadAsync<RecentUpdates>("extra/stats/most-recently-updated?" + scope + "&entries=24", token)
            .ConfigureAwait(false);
        if (recent.Items is null)
            throw new InvalidDataException("Universalis 返回的最近更新数据无效");
        return recent.Items.Where(item => item is { ItemId: > 0, LastUploadTime: > 0 })
            .OrderByDescending(item => item.LastUploadTime).DistinctBy(item => item.ItemId).Take(8).ToArray();
    }

    public async Task<MarketBoard?> GetBoardAsync(int worldId, string? dataCenter, int itemId, CancellationToken token)
    {
        var scope = dataCenter is null ? Id(worldId) : Uri.EscapeDataString(dataCenter);
        try
        {
            var board = await ReadAsync<MarketBoard>($"{scope}/{Id(itemId)}?listings=25&entries=15&fields={BoardFields}", token)
                .ConfigureAwait(false);
            if (board.ItemId != itemId || board.Listings is null || board.RecentHistory is null)
                throw new InvalidDataException("Universalis 返回的市价数据无效");
            return board with
            {
                Listings = board.Listings.Where(Valid).OrderBy(row => row.PricePerUnit).ToArray(),
                RecentHistory = board.RecentHistory.Where(Valid).OrderByDescending(row => row.Timestamp).ToArray(),
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.UserAgent.ParseAdd("Lodestone/1.0");
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Universalis 返回空数据");
    }

    private static bool Valid(MarketPrice row) => row is { PricePerUnit: > 0, Quantity: > 0 };
    private static string Id(int value) => value.ToString(CultureInfo.InvariantCulture);
    public void Dispose() => http.Dispose();

    private sealed record DataCenter(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("region")] string Region,
        [property: JsonPropertyName("worlds")] int[] Worlds);
    private sealed record DirectorySnapshot(MarketDataCenter[] DataCenters, DateTimeOffset Expires);
    private sealed record RecentUpdates([property: JsonPropertyName("items")] MarketUpdate[] Items);
}
