using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lodestone.Core.Assets;

internal sealed class XivApiExdImporter(HttpClient http)
{
    private const string Api = "https://xivapi-v2.xivcdn.com/api";
    private const string Language = "chs";
    private const int PageSize = 500;
    private const int ConcurrentDownloads = 4;
    private const ulong WeaponMask = (1UL << 48) - 1;
    private static readonly string[] Slots = ["MainHand", "OffHand", "Head", "Body", "Gloves", "Legs", "Feet",
        "Ears", "Neck", "Wrists", "FingerL", "FingerR"];
    private static readonly (string Name, string Fields)[] Sheets =
    [
        ("EquipSlotCategory", string.Join(',', Slots)),
        ("ItemUICategory", "Name"),
        ("ClassJobCategory", "Name"),
        ("BaseParam", "Name"),
        ("Stain", "Name,Color"),
        ("World", "Name,IsPublic,DataCenter.Region@as(raw)"),
        ("GuardianDeity", "Name"),
        ("Item", "Name,Icon@as(raw),ModelMain,ModelSub,EquipSlotCategory@as(raw),ItemUICategory@as(raw),"
            + "ClassJobCategory@as(raw),Rarity,LevelItem@as(raw),LevelEquip,DamagePhys,DamageMag,DefensePhys,DefenseMag,DyeCount,"
            + "BaseParam@as(raw),BaseParamValue,IsUntradable,ItemSearchCategory@as(raw)"),
        ("TerritoryType", "Name,Map@as(raw),PlaceName.Name,PlaceNameRegion.Name,TerritoryIntendedUse@as(raw),WeatherRate@as(raw)"),
        ("WeatherRate", "Rate,Weather@as(raw)"),
        ("Weather", "Name,Icon")
    ];
    private static readonly HashSet<int> MapTypes = [0, 1, 13, 21, 41, 48, 49, 56, 60, 61];
    private static readonly Dictionary<int, (string Id, string Name)> SpecialRegions = new()
    {
        [41] = ("eureka", "优雷卡"), [48] = ("bozja", "博兹雅"),
        [49] = ("island", "无人岛开拓"), [61] = ("crescent", "新月岛")
    };

    internal async Task<byte[]> DownloadAsync(Action<string>? progress, CancellationToken token)
    {
        var (firstSheet, firstFields) = Sheets[0];
        progress?.Invoke($"正在下载 {firstSheet}（0/{Sheets.Length} 张表已完成）");
        var (firstRows, release) = await ReadSheetAsync(firstSheet, firstFields, null, count =>
            progress?.Invoke($"正在下载 {firstSheet} · 已读取 {count:N0} 行（0/{Sheets.Length} 张表已完成）"), token).ConfigureAwait(false);
        var rows = new Dictionary<int, JsonElement>[Sheets.Length];
        rows[0] = firstRows;
        var completed = 1;
        var progressGate = new object();
        // Start the largest sheet early. Each sheet still follows its own pagination cursor in order.
        var remaining = Enumerable.Range(1, Sheets.Length - 1).OrderBy(index => Sheets[index].Name != "Item");
        await Parallel.ForEachAsync(remaining, new ParallelOptions { MaxDegreeOfParallelism = ConcurrentDownloads, CancellationToken = token },
            async (index, cancellationToken) =>
            {
                var (name, fields) = Sheets[index];
                void Report(int count)
                {
                    lock (progressGate)
                        progress?.Invoke($"正在下载 {name} · 已读取 {count:N0} 行（{completed}/{Sheets.Length} 张表已完成）");
                }
                (rows[index], _) = await ReadSheetAsync(name, fields, release, Report, cancellationToken).ConfigureAwait(false);
                lock (progressGate)
                {
                    completed++;
                    progress?.Invoke($"{completed}/{Sheets.Length} 张表已下载 · {name}");
                }
            }).ConfigureAwait(false);
        var tables = Sheets.Select((sheet, index) => (sheet.Name, Rows: rows[index]))
            .ToDictionary(sheet => sheet.Name, sheet => sheet.Rows, StringComparer.Ordinal);

        var weather = await ReadWeatherAsync(tables["TerritoryType"], tables["WeatherRate"], tables["Weather"],
            progress, token).ConfigureAwait(false);
        var items = new List<object[]>();
        var marketItems = new List<object[]>();
        foreach (var (id, row) in tables["Item"])
        {
            token.ThrowIfCancellationRequested();
            if (Equipment(id, row, tables) is { } item)
                items.Add(item);
            if (id > 0 && row.TryGetProperty("IsUntradable", out var untradable) && untradable.ValueKind == JsonValueKind.False
                && Number(row, "ItemSearchCategory@as(raw)") > 0 && Name(row) is { Length: > 0 } name)
                marketItems.Add([id, name, Number(row, "Icon@as(raw)")]);
        }

        var snapshot = new
        {
            format = ExdCatalog.Format,
            source = new { api = Api, language = Language, version = release.Version, schema = release.Schema },
            sources = tables.ToDictionary(pair => pair.Key, pair =>
                Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(pair.Value)))),
            wardrobeSlotCoverage = true,
            items,
            marketItems = marketItems.Count > 0 ? marketItems : null,
            stains = tables["Stain"].Where(pair => pair.Key > 0).Select(pair =>
            {
                var name = Name(pair.Value);
                return new object[] { pair.Key, name.Length > 0 ? name : $"染色 {pair.Key}",
                    $"#{Model(pair.Value, "Color") & 0xffffff:x6}" };
            }),
            worlds = WorldNames(tables["World"]),
            guardianDeities = tables["GuardianDeity"].Where(pair => pair.Key > 0)
                .Select(pair => (pair.Key, Name: Name(pair.Value))).Where(pair => pair.Name.Length > 0)
                .Select(pair => new object[] { pair.Key, pair.Name }),
            weather
        };
        using var output = new MemoryStream();
        using (var compressed = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            await JsonSerializer.SerializeAsync(compressed, snapshot, cancellationToken: token).ConfigureAwait(false);
        return output.ToArray();
    }

    private async Task<(Dictionary<int, JsonElement> Rows, Release Release)> ReadSheetAsync(
        string sheet, string fields, Release? release, Action<int> progress, CancellationToken token)
    {
        var query = FormattableString.Invariant(
            $"sheet/{sheet}?language={Language}&fields={Uri.EscapeDataString(fields)}&limit={PageSize}");
        var result = new Dictionary<int, JsonElement>();
        var previous = -1;
        while (true)
        {
            var url = query;
            if (release is not null)
                url += "&schema=" + Uri.EscapeDataString(release.Schema);
            if (previous >= 0)
                url += "&after=" + previous.ToString(CultureInfo.InvariantCulture);
            using var response = await GetAsync(url, "application/json", token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var page = json.RootElement;
            var version = new Release(page.GetProperty("version").GetString() ?? "", page.GetProperty("schema").GetString() ?? "");
            if (string.IsNullOrWhiteSpace(version.Version) || string.IsNullOrWhiteSpace(version.Schema))
                throw new InvalidDataException($"XIVAPI 的 {sheet} 表缺少版本信息");
            if (release is not null && version != release)
                throw new InvalidDataException("XIVAPI 数据版本在下载期间发生变化，请重试");
            release = version;

            var rows = page.GetProperty("rows");
            if (rows.GetArrayLength() == 0)
                return result.Count > 0 ? (result, release) : throw new InvalidDataException($"XIVAPI 的 {sheet} 表为空");
            foreach (var row in rows.EnumerateArray())
            {
                var id = Number(row, "row_id");
                if (id <= previous)
                    throw new InvalidDataException($"XIVAPI 的 {sheet} 表分页顺序无效");
                result.Add(id, row.GetProperty("fields").Clone());
                previous = id;
            }
            progress(result.Count);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string mediaType, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? delay = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/{path}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
                request.Headers.UserAgent.ParseAdd("Lodestone/1.0");
                var response = await http.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return response;
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        throw new HttpRequestException("XIVAPI 未找到请求的数据", null, response.StatusCode);
                    if (attempt == 3 || (int)response.StatusCode is not (429 or 500 or 502 or 503 or 504))
                        throw new HttpRequestException($"XIVAPI 返回 HTTP {(int)response.StatusCode}", null, response.StatusCode);
                    var retry = response.Headers.RetryAfter;
                    delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
                }
            }
            catch (HttpRequestException ex) when (attempt < 3 && ex.StatusCode is null) { }
            catch (OperationCanceledException) when (attempt < 3 && !token.IsCancellationRequested) { }
            var seconds = Math.Clamp(delay?.TotalSeconds ?? (1 << attempt), 0, 30);
            await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
        }
    }

    private static object[]? Equipment(int id, JsonElement item, Dictionary<string, Dictionary<int, JsonElement>> tables)
    {
        var name = Name(item);
        var main = Model(item, "ModelMain");
        var sub = Model(item, "ModelSub") & WeaponMask;
        if (name.Length == 0 || (main | sub) == 0)
            return null;

        var slots = tables["EquipSlotCategory"][Number(item, "EquipSlotCategory@as(raw)")];
        var models = new List<ulong[]>();
        var mainHand = Number(slots, "MainHand") > 0;
        for (var index = 0; index < Slots.Length; index++)
        {
            var model = main & (index < 2 ? WeaponMask : uint.MaxValue);
            if ((model & 0xffff) != 0 && Number(slots, Slots[index]) > 0 && !(index == 1 && mainHand))
                models.Add([(ulong)index, model]);
        }
        // Paired weapons use ModelSub even if they block equipping a separate off hand.
        if (mainHand && (sub & 0xffff) != 0)
            models.Add([1, sub]);
        if (models.Count == 0)
            return null;

        var attributes = new List<object[]>();
        foreach (var (param, value) in PairedNumbers(item, "BaseParam@as(raw)", "BaseParamValue"))
        {
            if (param == 0 || value == 0)
                continue;
            var label = Name(tables["BaseParam"][param]);
            if (label.Length > 0)
                attributes.Add([label, value]);
        }

        object[] details =
        [
            Name(tables["ItemUICategory"][Number(item, "ItemUICategory@as(raw)")]),
            Name(tables["ClassJobCategory"][Number(item, "ClassJobCategory@as(raw)")]),
            Number(item, "Rarity"), Number(item, "LevelItem@as(raw)"), Number(item, "LevelEquip"),
            Number(item, "DamagePhys"), Number(item, "DamageMag"), Number(item, "DefensePhys"), Number(item, "DefenseMag"),
            attributes
        ];
        var coveredSlots = 0;
        for (var index = 2; index < Slots.Length; index++)
            if (Number(slots, Slots[index]) < 0)
                coveredSlots |= 1 << index;
        return [id, name, Number(item, "Icon@as(raw)"), models, details, coveredSlots, Number(item, "DyeCount")];
    }

    private static IEnumerable<object[]> WorldNames(Dictionary<int, JsonElement> rows)
    {
        foreach (var (id, row) in rows)
        {
            var name = Name(row);
            var center = row.GetProperty("DataCenter");
            var regionalCenter = center.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("Region@as(raw)", out var region) && region.TryGetInt32(out var value) && value is 5 or 6;
            // Retired CN and Korean world IDs can be missing a datacenter.
            var regionalName = name.Any(character => character is >= '\u3400' and <= '\u9fff' or >= '\uac00' and <= '\ud7af');
            if (id is > 0 and < ushort.MaxValue && name.Length > 0 && (row.GetProperty("IsPublic").GetBoolean() || regionalCenter || regionalName))
                yield return [id, name];
        }
    }

    private async Task<WeatherCatalog> ReadWeatherAsync(Dictionary<int, JsonElement> territories,
        Dictionary<int, JsonElement> rates, Dictionary<int, JsonElement> weather, Action<string>? progress, CancellationToken token)
    {
        var regions = new Dictionary<string, (string Name, List<WeatherMap> Maps)>(StringComparer.Ordinal);
        var usedWeather = new SortedSet<int>();
        foreach (var (id, row) in territories)
        {
            var use = Number(row, "TerritoryIntendedUse@as(raw)");
            // Diadem has a distinct intended use from the other supported exploration areas.
            if ((!MapTypes.Contains(use) && id != 656) || Name(row).EndsWith("_test", StringComparison.Ordinal))
                continue;
            var rateId = Number(row, "WeatherRate@as(raw)");
            var name = RelatedName(row.GetProperty("PlaceName"));
            if (rateId == 0 || name.Length == 0 || Number(row, "Map@as(raw)") == 0)
                continue;

            var place = row.GetProperty("PlaceNameRegion");
            var (regionId, regionName) = SpecialRegions.TryGetValue(use, out var special)
                ? special : (Number(place, "value").ToString(CultureInfo.InvariantCulture), RelatedName(place));
            if (regionName.Length == 0)
                throw new InvalidDataException($"XIVAPI 的地图 {name} 缺少地区");

            var distribution = new List<WeatherRate>();
            var upper = 0;
            foreach (var (weatherId, chance) in PairedNumbers(rates[rateId], "Weather@as(raw)", "Rate"))
            {
                if (chance == 0)
                    continue;
                if (weatherId <= 0 || chance < 0 || chance > 100 - upper || Name(weather[weatherId]).Length == 0)
                    throw new InvalidDataException($"XIVAPI 的 WeatherRate/{rateId} 包含无效的概率");
                upper += chance;
                distribution.Add(new(weatherId, upper));
                usedWeather.Add(weatherId);
            }
            if (distribution.Count == 0)
                throw new InvalidDataException($"XIVAPI 的 WeatherRate/{rateId} 缺少概率");
            // The final weather covers remaining rolls, including Empyreum's 90% table.
            distribution[^1] = distribution[^1] with { Upper = 100 };
            if (!regions.TryGetValue(regionId, out var region))
                regions.Add(regionId, region = (regionName, []));
            region.Maps.Add(new(id, name, distribution.ToArray()));
        }

        var entries = usedWeather.Select(id =>
        {
            var icon = weather[id].GetProperty("Icon");
            return (Id: id, Path: icon.GetProperty("path_hr1").GetString() ?? "", Icon: icon);
        }).ToArray();
        var groups = entries.GroupBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var icons = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var completed = 0;
        var progressGate = new object();
        progress?.Invoke($"正在下载天气图标（0/{groups.Length}）");
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = ConcurrentDownloads, CancellationToken = token },
            async (group, cancellationToken) =>
            {
                byte[] bytes;
                try { bytes = await ReadIconAsync(group.Key, cancellationToken).ConfigureAwait(false); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    var fallback = group.First().Icon.GetProperty("path").GetString() ?? "";
                    bytes = await ReadIconAsync(fallback, cancellationToken).ConfigureAwait(false);
                }
                icons[group.Key] = bytes;
                lock (progressGate)
                {
                    completed++;
                    progress?.Invoke($"正在下载天气图标（{completed}/{groups.Length}）");
                }
            }).ConfigureAwait(false);
        var kinds = entries.ToDictionary(entry => entry.Id, entry => new WeatherKind(Name(weather[entry.Id]), icons[entry.Path]));
        return new(regions.Select(pair => new WeatherRegion(pair.Key, pair.Value.Name, pair.Value.Maps.ToArray())).ToArray(), kinds);
    }

    private async Task<byte[]> ReadIconAsync(string path, CancellationToken token)
    {
        if (!path.StartsWith("ui/icon/", StringComparison.Ordinal) || !path.EndsWith(".tex", StringComparison.Ordinal))
            throw new InvalidDataException("XIVAPI 返回了无效的天气图标路径");
        using var response = await GetAsync($"asset?path={Uri.EscapeDataString(path)}&format=webp", "image/webp", token)
            .ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        return ExdCatalog.IsWebp(bytes) ? bytes : throw new InvalidDataException("XIVAPI 返回了无效的天气图标");
    }

    private static IEnumerable<(int, int)> PairedNumbers(JsonElement row, string first, string second)
    {
        var left = row.GetProperty(first);
        var right = row.GetProperty(second);
        if (left.GetArrayLength() != right.GetArrayLength())
            throw new InvalidDataException($"XIVAPI 的 {first} 和 {second} 字段长度不一致");
        return left.EnumerateArray().Zip(right.EnumerateArray(), (a, b) => (OptionalNumber(a), OptionalNumber(b)));
    }

    private static ulong Model(JsonElement item, string field)
    {
        var value = item.GetProperty(field);
        return value.TryGetUInt64(out var model) ? model : unchecked((ulong)value.GetInt64());
    }

    private static int Number(JsonElement row, string field) => row.GetProperty(field).GetInt32();
    private static int OptionalNumber(JsonElement value) => value.ValueKind == JsonValueKind.Null ? 0 : value.GetInt32();
    private static string Name(JsonElement row) => row.GetProperty("Name").GetString()?.Trim() ?? "";
    private static string RelatedName(JsonElement relation) =>
        relation.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object
            && fields.TryGetProperty("Name", out var name) ? name.GetString()?.Trim() ?? "" : "";

    private sealed record Release(string Version, string Schema);
}
