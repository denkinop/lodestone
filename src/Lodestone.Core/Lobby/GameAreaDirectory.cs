using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lodestone.Core.Lobby;

public sealed class GameArea
{
    internal GameArea(int id, string name, string host, int order) =>
        (Id, Name, Host, Order) = (id, name, host, order);
    public int Id { get; }
    public string Name { get; }
    internal string Host { get; }
    internal int Order { get; }
}

public sealed partial class GameAreaDirectory(string? cacheFile = null) : IDisposable
{
    private static readonly Uri DirectoryUri = new("https://ff.dorado.sdo.com/ff/area/serverlist_new.js");
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<IReadOnlyList<GameArea>> GetAsync(CancellationToken cancellationToken = default, bool refresh = false)
    {
        if (!refresh && cacheFile is not null)
        {
            try
            {
                if (new FileInfo(cacheFile) is { Exists: true, Length: <= 65536 })
                    return Parse(await File.ReadAllTextAsync(cacheFile, cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException
                or KeyNotFoundException or InvalidOperationException) { }
        }
        try
        {
            var script = await http.GetStringAsync(DirectoryUri, cancellationToken);
            var areas = Parse(script);
            if (cacheFile is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                    await File.WriteAllTextAsync(cacheFile, script, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return areas;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or KeyNotFoundException
            or InvalidOperationException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new LobbyException("无法读取官方大区目录");
        }
    }

    internal static IReadOnlyList<GameArea> Parse(string value)
    {
        var script = value.Trim().TrimEnd(';').TrimEnd();
        const string prefix = "var servers=";
        if (script.Length > 65536 || !script.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException();
        using var document = JsonDocument.Parse(script[prefix.Length..]);
        var areas = new Dictionary<int, GameArea>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("AreaStat").GetInt32() != 1)
                continue;
            var idText = entry.GetProperty("Areaid").GetString();
            var name = entry.GetProperty("AreaName").GetString();
            var host = entry.GetProperty("AreaLobby").GetString();
            var order = entry.GetProperty("AreaOrder").GetInt32();
            if (int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && id > 0 && !string.IsNullOrWhiteSpace(name) && host is not null && LobbyHost().IsMatch(host))
                areas.TryAdd(id, new GameArea(id, name, host, order));
        }
        if (areas.Count == 0)
            throw new FormatException();
        return areas.Values.OrderBy(area => area.Order).ThenBy(area => area.Id).ToArray();
    }

    [GeneratedRegex(@"\Affxivlobby[0-9]+\.ff14\.sdo\.com\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LobbyHost();

    public void Dispose() => http.Dispose();
}
