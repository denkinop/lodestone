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
    private const int DetailCacheLimit = 32;
    private const int MaximumCachedDocumentBytes = 256 * 1024;
    private static readonly TimeSpan DetailCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> Types = [
        "item", "quest", "achievement", "instance", "action", "status", "fate", "leve", "node", "fishing", "npc", "mob", "patch"
    ];
    private readonly HttpClient http;
    private readonly Func<ExdCatalog> exd;
    private readonly TimeProvider clock;
    private readonly Dictionary<(string Type, string Id), CachedDetail> details = [];
    private ExdCatalog? detailCatalog;
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

    internal GarlandDatabaseClient(HttpClient http, Func<ExdCatalog>? exd = null, TimeProvider? clock = null)
    {
        this.http = http;
        this.exd = exd ?? (() => ExdCatalog.Bundled);
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<GarlandEntry[]> SearchAsync(string query, string type, CancellationToken token) =>
        (await SearchWithStatusAsync(query, type, token).ConfigureAwait(false)).Entries;

    public async Task<(GarlandEntry[] Entries, bool LocalOnly)> SearchWithStatusAsync(string query, string type, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (type.Length > 0 && !Types.Contains(type) || query.Length > 100)
            return ([], false);
        query = query.Trim();
        if (type == "patch")
        {
            var data = await GetCoreAsync(token).ConfigureAwait(false);
            var patches = Get(Get(data, "patch"), "partialIndex");
            if (patches.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Garland Tools 版本目录格式无效");
            var entries = patches.EnumerateObject().Select(p => ReadEntry("patch", p.Name, p.Value))
                .OfType<GarlandEntry>().Where(entry => query.Length == 0
                    || entry.Id.Contains(query, StringComparison.Ordinal) || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => decimal.Parse(entry.Id, CultureInfo.InvariantCulture)).ToArray();
            return (entries, false);
        }
        if (query.Length == 0)
            return ([], false);
        if (type.Length > 0 && TryEntryId(query.Normalize(NormalizationForm.FormKC).TrimStart('#'), out var id))
        {
            if (type == "item" && FindLocalItem(exd(), id) is { } localItem)
                return ([localItem], false);
            var detail = await GetDetailAsync(type, id, token).ConfigureAwait(false);
            return (detail is null ? [] : [detail.Entry], false);
        }

        var includesItems = type is "" or "item";
        GarlandEntry[] remote;
        try
        {
            remote = await SearchRemoteAsync(query, type, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (includesItems && CanUseFallback(ex, token))
        {
            token.ThrowIfCancellationRequested();
            var fallback = SearchLocalItems(query);
            if (fallback.Length == 0)
                throw;
            return (fallback, true);
        }
        if (!includesItems)
            return (remote, false);
        var local = SearchLocalItems(query);
        if (local.Length == 0)
            return (remote, false);
        var itemResults = remote.Where(entry => entry.Type == "item").Concat(local)
            .DistinctBy(entry => entry.Id)
            .OrderBy(entry => DatabaseItemCatalog.MatchRank(entry.Name, query)).ThenBy(entry => entry.Name.Length)
            .Take(ItemSearchLimit);
        var merged = type == "item" ? itemResults : remote.Where(entry => entry.Type != "item").Concat(itemResults)
            .OrderBy(entry => DatabaseItemCatalog.MatchRank(entry.Name, query));
        return (merged.ToArray(), false);
    }

    private async Task<GarlandEntry[]> SearchRemoteAsync(string query, string type, CancellationToken token)
    {
        var path = "/api/search.php?lang=chs&text=" + Uri.EscapeDataString(query)
            + (type.Length > 0 ? "&type=" + type : "");
        var (result, _) = await ReadAsync(path, token).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Garland Tools 搜索结果格式无效");
        return result.EnumerateArray().Select(ReadPartial).OfType<GarlandEntry>()
            .Where(entry => type.Length == 0 || entry.Type == type)
            .DistinctBy(entry => (entry.Type, entry.Id)).Take(SearchLimit).ToArray();
    }

    private GarlandEntry[] SearchLocalItems(string query) =>
        exd().DatabaseItems.Search(query, ItemSearchLimit)
            .Select(item => LocalItemEntry(item, item.Id.ToString(CultureInfo.InvariantCulture))).ToArray();

    private static GarlandEntry? FindLocalItem(ExdCatalog catalog, string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId)
            && catalog.DatabaseItems.Find(itemId) is { } item ? LocalItemEntry(item, id) : null;

    private static GarlandEntry LocalItemEntry(DatabaseItem item, string id) =>
        new("item", id, item.Name, item.Icon, item.ItemLevel > 0 ? FormattableString.Invariant($"品级 {item.ItemLevel}") : null);

    private static bool CanUseFallback(Exception error, CancellationToken token) => !token.IsCancellationRequested
        && error is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException;

    public async Task<GarlandDetail?> GetDetailAsync(string type, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Types.Contains(type) || !ValidId(type, id))
            return null;
        var catalog = exd();
        var key = (type, id);
        lock (details)
        {
            if (!ReferenceEquals(detailCatalog, catalog))
            {
                details.Clear();
                detailCatalog = catalog;
            }
            if (details.TryGetValue(key, out var cached) && cached.Expires > clock.GetUtcNow())
                return cached.Detail;
        }
        var version = type is "item" or "leve" ? 3 : 2;
        JsonElement doc;
        long? bytes;
        try
        {
            (doc, bytes) = await ReadAsync($"/db/doc/{type}/chs/{version}/{id}.json", token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }

        var record = Get(doc, type);
        if (ReadEntry(type, id, record) is not { } entry || Text(record, "id") != id)
            throw new InvalidDataException("Garland Tools 条目格式无效");
        var location = Text(record, "location");
        var zone = ReadPlaceId(record, "zoneid");
        var area = ReadPlaceId(record, "areaid");
        var place = zone > 0 ? zone : area;
        var cacheable = bytes is <= MaximumCachedDocumentBytes;
        if (string.IsNullOrWhiteSpace(location) && place > 0)
        {
            try
            {
                var data = await GetCoreAsync(token).ConfigureAwait(false);
                location = Text(Get(Get(data, "locationIndex"), place.ToString(CultureInfo.InvariantCulture)), "name");
            }
            catch (Exception ex) when (CanUseFallback(ex, token))
            {
                cacheable = false;
            }
        }
        var coordinates = ReadCoordinates(record);
        GarlandMap? map = null;
        if ((place > 0 || !string.IsNullOrWhiteSpace(location)) && catalog.Maps.Find(zone, area, location) is { } gameMap)
        {
            location = Clean(location) ?? gameMap.DisplayName;
            map = new(gameMap.Id, location, coordinates?.X, coordinates?.Y);
        }
        var detail = Describe(entry, record, doc, location, coordinates, map, catalog);
        token.ThrowIfCancellationRequested();
        if (cacheable)
        {
            lock (details)
            {
                if (ReferenceEquals(detailCatalog, catalog))
                {
                    if (details.Count >= DetailCacheLimit && !details.ContainsKey(key))
                        details.Remove(details.MinBy(pair => pair.Value.Expires).Key);
                    details[key] = new(detail, clock.GetUtcNow() + DetailCacheLifetime);
                }
            }
        }
        return detail;
    }

    private static int ReadPlaceId(JsonElement record, string field) =>
        int.TryParse(Text(record, field), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private async Task<JsonElement> GetCoreAsync(CancellationToken token)
    {
        var cached = Volatile.Read(ref core);
        if (cached is not null && cached.Expires > clock.GetUtcNow())
            return cached.Data;
        var (data, _) = await ReadAsync("/db/doc/core/chs/3/data.json", token).ConfigureAwait(false);
        if (Get(data, "patch").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Garland Tools 目录格式无效");
        Volatile.Write(ref core, new(data, clock.GetUtcNow().AddHours(1)));
        return data;
    }

    private async Task<(JsonElement Data, long? Bytes)> ReadAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.UserAgent.ParseAdd("Lodestone/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        return (doc.RootElement.Clone(), stream.CanSeek ? stream.Length : null);
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
    private sealed record CachedDetail(GarlandDetail Detail, DateTimeOffset Expires);
    private sealed record CoreSnapshot(JsonElement Data, DateTimeOffset Expires);
}
