using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lodestone.Core.Assets;

public sealed record WeatherCatalog(
    [property: JsonPropertyName("regions")] WeatherRegion[] Regions,
    [property: JsonPropertyName("weather")] IReadOnlyDictionary<int, WeatherKind> Weather);

public sealed record WeatherRegion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("maps")] WeatherMap[] Maps);

public sealed record WeatherMap(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rates")] WeatherRate[] Rates);

public sealed record WeatherRate(
    [property: JsonPropertyName("weather")] int Weather,
    [property: JsonPropertyName("upper")] int Upper);

public sealed record WeatherKind(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] byte[] Icon);

public sealed class ExdCatalog
{
    internal const int Format = 1;
    private static readonly Lazy<ExdCatalog> BuiltIn = new(LoadBundled);
    public static ExdCatalog Bundled => BuiltIn.Value;

    public string Version { get; }
    public CharacterData Character { get; }
    public WeatherCatalog Weather { get; }
    public MarketItemCatalog MarketItems { get; }

    private ExdCatalog(JsonElement data, ExdCatalog? fallback)
    {
        if (data.GetProperty("format").GetInt32() != Format)
            throw new InvalidDataException("不支持此 EXD 快照格式");
        var source = data.GetProperty("source");
        Version = source.GetProperty("version").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(source.GetProperty("schema").GetString())
            || source.GetProperty("language").GetString() != "chs")
            throw new InvalidDataException("EXD 缺少有效的简体中文发布信息");

        Weather = data.GetProperty("weather").Deserialize<WeatherCatalog>()
            ?? throw new InvalidDataException("EXD 缺少天气数据");
        Validate(Weather);
        Character = new CharacterData(data, fallback?.Character);
        MarketItems = data.TryGetProperty("marketItems", out var marketItems) && marketItems.ValueKind == JsonValueKind.Array
            ? MarketItemCatalog.Read(marketItems) : fallback?.MarketItems ?? MarketItemCatalog.Empty;
    }

    private static ExdCatalog LoadBundled()
    {
        using var resource = typeof(ExdCatalog).Assembly.GetManifestResourceStream("Lodestone.Data.Exd")
            ?? throw new InvalidOperationException("缺少内置 EXD 快照");
        return Read(resource);
    }

    internal static ExdCatalog Read(Stream compressed, ExdCatalog? fallback = null)
    {
        using var input = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        using var json = JsonDocument.Parse(input);
        return new ExdCatalog(json.RootElement, fallback);
    }

    internal static bool IsWebp(ReadOnlySpan<byte> content) =>
        content.Length >= 20 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8);

    private static void Validate(WeatherCatalog catalog)
    {
        if (catalog.Regions is not { Length: > 0 } || catalog.Weather is not { Count: > 0 }
            || catalog.Weather.Any(entry => entry.Key <= 0 || entry.Value is null
                || string.IsNullOrWhiteSpace(entry.Value.Name) || !IsWebp(entry.Value.Icon)))
            throw new InvalidDataException("EXD 缺少有效的天气或图标");

        var regions = new HashSet<string>(StringComparer.Ordinal);
        var maps = new HashSet<int>();
        foreach (var region in catalog.Regions)
        {
            if (region is null || string.IsNullOrWhiteSpace(region.Id) || !regions.Add(region.Id)
                || string.IsNullOrWhiteSpace(region.Name) || region.Maps is not { Length: > 0 })
                throw new InvalidDataException("EXD 包含无效的天气地区");
            foreach (var map in region.Maps)
            {
                if (map is null || map.Id <= 0 || !maps.Add(map.Id) || string.IsNullOrWhiteSpace(map.Name)
                    || map.Rates is not { Length: > 0 } || map.Rates[^1]?.Upper != 100)
                    throw new InvalidDataException("EXD 包含无效的天气地图");
                var lower = 0;
                foreach (var rate in map.Rates)
                {
                    if (rate is null || rate.Upper <= lower || rate.Upper > 100 || !catalog.Weather.ContainsKey(rate.Weather))
                        throw new InvalidDataException("EXD 包含无效的天气概率");
                    lower = rate.Upper;
                }
            }
        }
    }
}

public sealed class ExdDataStore
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string path;
    private readonly XivApiExdImporter importer;
    private readonly SemaphoreSlim refreshLock = new(1);
    private readonly Lazy<ExdCatalog> initial;
    private ExdCatalog? updated;

    public ExdDataStore(string path) : this(path, Http) { }

    internal ExdDataStore(string path, HttpClient http)
    {
        this.path = Path.GetFullPath(path);
        importer = new XivApiExdImporter(http);
        initial = new Lazy<ExdCatalog>(Load);
    }

    public ExdCatalog Current => Volatile.Read(ref updated) ?? initial.Value;

    public async Task<ExdCatalog> RefreshAsync(Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await importer.DownloadAsync(progress, cancellationToken).ConfigureAwait(false);
            progress?.Invoke("正在校验并保存 EXD 快照…");
            using var source = new MemoryStream(snapshot, writable: false);
            var catalog = ExdCatalog.Read(source, ExdCatalog.Bundled);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, snapshot, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Volatile.Write(ref updated, catalog);
            return catalog;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private ExdCatalog Load()
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return ExdCatalog.Read(input, ExdCatalog.Bundled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
            or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException
            or IndexOutOfRangeException)
        {
            return ExdCatalog.Bundled;
        }
    }
}
