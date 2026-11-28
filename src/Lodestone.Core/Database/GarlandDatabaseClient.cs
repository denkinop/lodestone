using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lodestone.Core.Assets;

namespace Lodestone.Core.Database;

public sealed record GarlandEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] int Icon,
    [property: JsonPropertyName("summary")] string? Summary);
public sealed record GarlandField(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value);
public sealed record GarlandLinks(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("entries")] GarlandEntry[] Entries);
public sealed record GarlandMap(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("x")] decimal? X,
    [property: JsonPropertyName("y")] decimal? Y);
public sealed record GarlandDetail(
    [property: JsonPropertyName("entry")] GarlandEntry Entry,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("fields")] GarlandField[] Fields,
    [property: JsonPropertyName("links")] GarlandLinks[] Links,
    [property: JsonPropertyName("map")] GarlandMap? Map = null);

public sealed partial class GarlandDatabaseClient : IDisposable
{
    private const string BaseUrl = "https://www.garlandtools.cn";
    private const int SearchLimit = 100;
    private const int ItemSearchLimit = 200;
    private static readonly HashSet<string> Types = [
        "item", "quest", "achievement", "instance", "action", "status", "fate", "leve", "node", "fishing", "npc", "mob", "patch"
    ];
    private readonly HttpClient http;
    private readonly Func<ExdCatalog> exd;
    private CoreSnapshot? core;

    public GarlandDatabaseClient(Func<ExdCatalog>? exd = null) : this(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 8 * 1024 * 1024
    }, exd) { }

    internal GarlandDatabaseClient(HttpClient http, Func<ExdCatalog>? exd = null)
    {
        this.http = http;
        this.exd = exd ?? (() => ExdCatalog.Bundled);
    }

    public async Task<GarlandEntry[]> SearchAsync(string query, string type, CancellationToken token)
    {
        if (type.Length > 0 && !Types.Contains(type) || query.Length > 100)
            return [];
        query = query.Trim();
        if (type == "patch")
        {
            var data = await GetCoreAsync(token).ConfigureAwait(false);
            var patches = Get(Get(data, "patch"), "partialIndex");
            if (patches.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Garland Tools 版本目录格式无效");
            return patches.EnumerateObject().Select(p => ReadEntry("patch", p.Name, p.Value))
                .OfType<GarlandEntry>().Where(entry => query.Length == 0
                    || entry.Id.Contains(query, StringComparison.Ordinal) || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => decimal.Parse(entry.Id, CultureInfo.InvariantCulture)).ToArray();
        }
        if (query.Length == 0)
            return [];
        if (type.Length > 0 && TryEntryId(query.Normalize(NormalizationForm.FormKC).TrimStart('#'), out var id))
        {
            var detail = await GetDetailAsync(type, id, token).ConfigureAwait(false);
            return detail is null ? [] : [detail.Entry];
        }

        var path = "/api/search.php?lang=chs&text=" + Uri.EscapeDataString(query)
            + (type.Length > 0 ? "&type=" + type : "");
        var result = await ReadAsync(path, token).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Garland Tools 搜索结果格式无效");
        var remote = result.EnumerateArray().Select(ReadPartial).OfType<GarlandEntry>()
            .Where(entry => type.Length == 0 || entry.Type == type)
            .DistinctBy(entry => (entry.Type, entry.Id)).Take(SearchLimit).ToArray();
        if (type is not ("" or "item"))
            return remote;
        var local = exd().DatabaseItems.Search(query, ItemSearchLimit);
        if (local.Length == 0)
            return remote;
        var itemResults = remote.Where(entry => entry.Type == "item")
            .Concat(local.Select(item => new GarlandEntry("item", item.Id.ToString(CultureInfo.InvariantCulture),
                item.Name, item.Icon, item.ItemLevel > 0 ? FormattableString.Invariant($"品级 {item.ItemLevel}") : null)))
            .DistinctBy(entry => entry.Id)
            .OrderBy(entry => DatabaseItemCatalog.MatchRank(entry.Name, query)).ThenBy(entry => entry.Name.Length)
            .Take(ItemSearchLimit);
        return type == "item" ? itemResults.ToArray()
            : remote.Where(entry => entry.Type != "item").Concat(itemResults)
                .OrderBy(entry => DatabaseItemCatalog.MatchRank(entry.Name, query)).ToArray();
    }

    public async Task<GarlandDetail?> GetDetailAsync(string type, string id, CancellationToken token)
    {
        if (!Types.Contains(type) || !ValidId(type, id))
            return null;
        var version = type is "item" or "leve" ? 3 : 2;
        JsonElement doc;
        try
        {
            doc = await ReadAsync($"/db/doc/{type}/chs/{version}/{id}.json", token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }

        var record = Get(doc, type);
        if (ReadEntry(type, id, record) is not { } entry || Text(record, "id") != id)
            throw new InvalidDataException("Garland Tools 条目格式无效");
        var location = Text(record, "location");
        var zone = ReadPlaceId(record, "zoneid");
        var area = ReadPlaceId(record, "areaid");
        var place = zone > 0 ? zone : area;
        if (string.IsNullOrWhiteSpace(location) && place > 0)
        {
            try
            {
                var data = await GetCoreAsync(token).ConfigureAwait(false);
                location = Text(Get(Get(data, "locationIndex"), place.ToString(CultureInfo.InvariantCulture)), "name");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                // Coordinates and entry data are still available if the location catalog is unreachable.
            }
        }
        var coordinates = ReadCoordinates(record);
        GarlandMap? map = null;
        if ((place > 0 || !string.IsNullOrWhiteSpace(location)) && exd().Maps.Find(zone, area, location) is { } gameMap)
        {
            location = Clean(location) ?? gameMap.DisplayName;
            map = new(gameMap.Id, location, coordinates?.X, coordinates?.Y);
        }
        return Describe(entry, record, doc, location, coordinates, map);
    }

    private static int ReadPlaceId(JsonElement record, string field) =>
        int.TryParse(Text(record, field), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private async Task<JsonElement> GetCoreAsync(CancellationToken token)
    {
        var cached = Volatile.Read(ref core);
        if (cached is not null && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Data;
        var data = await ReadAsync("/db/doc/core/chs/3/data.json", token).ConfigureAwait(false);
        if (Get(data, "patch").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Garland Tools 目录格式无效");
        Volatile.Write(ref core, new(data, DateTimeOffset.UtcNow.AddHours(1)));
        return data;
    }

    private async Task<JsonElement> ReadAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.UserAgent.ParseAdd("Lodestone/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private static GarlandEntry? ReadPartial(JsonElement row)
    {
        var type = Text(row, "type");
        var id = Text(row, "id");
        return type is null || id is null ? null : ReadEntry(type, id, Get(row, "obj"));
    }

    private static GarlandEntry? ReadEntry(string type, string id, JsonElement entry)
    {
        if (!Types.Contains(type) || !ValidId(type, id) || Clean(Text(entry, "name") ?? Text(entry, "n")) is not { } name)
            return null;
        var icon = Text(entry, "icon") ?? Text(entry, "eventIcon") ?? Text(entry, "categoryIcon");
        if (icon is null && type is "item" or "achievement" or "instance" or "status" or "action")
            icon = Text(entry, "c");
        int.TryParse(icon, NumberStyles.None, CultureInfo.InvariantCulture, out var iconId);
        var summary = type switch
        {
            "quest" => Text(entry, "location") ?? Text(entry, "l"),
            "item" => LevelText("品级", Text(entry, "ilvl") ?? Text(entry, "l")),
            "patch" => Text(entry, "series"),
            "instance" => Text(entry, "category") ?? Text(entry, "t"),
            "leve" or "fate" or "node" or "fishing" or "mob" => LevelText("等级", Text(entry, "lvl") ?? Text(entry, "l")),
            _ => null
        };
        return new(type, id, name, iconId, Clean(summary));
    }

    private static string? LevelText(string label, string? value) => value is null ? null : $"{label} {value}";

    private static bool ValidId(string type, string id) => type == "patch" ? PatchId().IsMatch(id)
        : TryEntryId(id, out var normalized) && normalized == id;

    private static bool TryEntryId(string value, out string id)
    {
        var valid = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
        id = valid ? number.ToString(CultureInfo.InvariantCulture) : "";
        return valid;
    }

    private static JsonElement Get(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : default;

    private static string? Text(JsonElement value, string key) => Scalar(Get(value, key));

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    private static IEnumerable<JsonElement> ArrayOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = WebUtility.HtmlDecode(value);
        text = Tags().Replace(text, match => match.Value.StartsWith("<br", StringComparison.OrdinalIgnoreCase)
            || match.Value.Equals("</p>", StringComparison.OrdinalIgnoreCase) ? "\n" : "").Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"\A[0-9]{1,2}\.[0-9]{1,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PatchId();
    [GeneratedRegex(@"</?[a-zA-Z][^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    public void Dispose() => http.Dispose();
    private sealed record CoreSnapshot(JsonElement Data, DateTimeOffset Expires);
}
