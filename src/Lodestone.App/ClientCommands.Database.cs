using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lodestone.Core.Database;

namespace Lodestone.App;

internal sealed partial class ClientCommands
{
    private readonly GarlandDatabaseClient garland = new(() => Exd.Current);

    public Task<DatabaseResponse<GarlandEntry[]>> SearchGameDatabase(string encodedQuery, string type) =>
        ReadDatabaseAsync<GarlandEntry[]>(async token =>
            await garland.SearchAsync(Uri.UnescapeDataString(encodedQuery), type, token).ConfigureAwait(false));

    public Task<DatabaseResponse<GarlandDetail>> GetGameDatabaseEntry(string type, string id) =>
        ReadDatabaseAsync(token => garland.GetDetailAsync(type, id, token));

    private async Task<DatabaseResponse<T>> ReadDatabaseAsync<T>(Func<CancellationToken, Task<T?>> read) where T : class
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
            return new(Error: ex.StatusCode == HttpStatusCode.TooManyRequests
                ? "查询过于频繁，请稍后重试。" : "无法连接 Garland Tools 国服站，请稍后重试。");
        }
        catch (OperationCanceledException) { return new(Error: "数据库查询超时，请重试。"); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return new(Error: "无法读取 Garland Tools 的数据，请稍后重试。");
        }
    }
}

internal sealed record DatabaseResponse<T>(
    [property: JsonPropertyName("data")] T? Data = null,
    [property: JsonPropertyName("error")] string? Error = null) where T : class;
