using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lodestone.App;

internal sealed class OfficialHomeClient : IDisposable
{
    private const string CalendarHost = "https://apiff14risingstones.web.sdo.com";
    private const string Portal = "https://ff14risingstones.web.sdo.com";
    private const string NewsPage = "https://ff.web.sdo.com/web8/index.html#/newstab/newscont/";
    private readonly HttpClient http = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public async Task<OfficialFeed<OfficialBanner>> GetBannersAsync()
    {
        var feed = await ReadAsync<NewsPayload>(NewsRequest("5203", 8), "0");
        OfficialBanner[] banners = feed?.Data?.Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .Select(item => new OfficialBanner(item.Id, item.Title!, SafeUrl(item.HomeImagePath), SafeUrl(item.OutLink) ?? NewsPage + item.Id))
            .ToArray() ?? [];
        return new(banners, feed is null);
    }

    public async Task<OfficialFeed<OfficialNews>> GetNewsAsync()
    {
        var feed = await ReadAsync<NewsPayload>(NewsRequest("8324,8325,8326,8327,5309,5310,5311,5312,5313", 16), "0");
        OfficialNews[] news = feed?.Data?.Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .Select(item => new OfficialNews(item.Id, item.Title!, item.PublishDate, item.CategoryCode is >= 8324 and <= 8327,
                SafeUrl(item.OutLink) ?? NewsPage + item.Id))
            .ToArray() ?? [];
        return new(news, feed is null);
    }

    public async Task<OfficialFeed<OfficialActivity>> GetCalendarAsync(string month)
    {
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return new([], true);

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{CalendarHost}/api/home/active/calendar/getActiveCalendarMonth?month={month}");
        request.Headers.Referrer = new(Portal + "/");
        request.Headers.TryAddWithoutValidation("Origin", Portal);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        var feed = await ReadAsync<CalendarPayload>(request, "10000");
        OfficialActivity[] activities = feed?.Data?.Where(item => !string.IsNullOrWhiteSpace(item.Name)
                && item.BeginTime > 0 && item.EndTime >= item.BeginTime)
            .OrderBy(item => item.BeginTime).ThenByDescending(item => item.Weight)
            .Select(item => new OfficialActivity(item.Id, item.Name!, item.BeginTime, item.EndTime, item.Color, SafeUrl(item.Url)))
            .ToArray() ?? [];
        return new(activities, feed is null);
    }

    private static HttpRequestMessage NewsRequest(string categories, int count)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode={categories}&pageIndex=0&pageSize={count}");
        request.Headers.Referrer = new("https://ff.web.sdo.com/project/launcher0904/index.html");
        request.Headers.Accept.ParseAdd("*/*");
        return request;
    }

    private async Task<T?> ReadAsync<T>(HttpRequestMessage request, string successCode) where T : Payload
    {
        using (request)
        {
            request.Headers.UserAgent.ParseAdd("Lodestone/0.1");
            request.Headers.AcceptLanguage.ParseAdd("zh-CN");
            try
            {
                using var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    return null;
                var payload = await response.Content.ReadFromJsonAsync<T>();
                return payload?.Code.ToString() == successCode ? payload : null;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or ObjectDisposedException)
            {
                return null;
            }
        }
    }

    internal static bool IsOfficialWebUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host.EndsWith(".web.sdo.com", StringComparison.Ordinal);

    private static string? SafeUrl(string? value)
    {
        if (value?.StartsWith("//", StringComparison.Ordinal) == true)
            value = "https:" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsDefaultPort)
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        return uri is not null && IsOfficialWebUri(uri) ? uri.AbsoluteUri : null;
    }

    public void Dispose() => http.Dispose();

    private abstract record Payload
    {
        public JsonElement Code { get; init; }
    }

    private sealed record NewsPayload : Payload
    {
        public NewsItem[]? Data { get; init; }
    }

    private sealed record CalendarPayload : Payload
    {
        public ActivityItem[]? Data { get; init; }
    }

    private sealed record NewsItem(long Id, string? Title, string? HomeImagePath, string? OutLink, string? PublishDate, int CategoryCode);

    private sealed record ActivityItem(int Id, string? Name,
        [property: JsonPropertyName("begin_time")] long BeginTime,
        [property: JsonPropertyName("end_time")] long EndTime,
        int Weight, string? Color, string? Url);
}

internal sealed record OfficialFeed<T>(
    [property: JsonPropertyName("items")] T[] Items,
    [property: JsonPropertyName("failed")] bool Failed);

internal sealed record OfficialBanner(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("url")] string Url);

internal sealed record OfficialNews(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("published")] string? Published,
    [property: JsonPropertyName("announcement")] bool Announcement,
    [property: JsonPropertyName("url")] string Url);

internal sealed record OfficialActivity(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("url")] string? Url);
