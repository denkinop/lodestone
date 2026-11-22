using System.Globalization;
using System.Net;
using Lodestone.Core.Auth;
using Lodestone.Core.Community;

namespace Lodestone.App;

internal sealed class CommunityBrowser
{
    private Connection? connection;

    internal void SetSession(RisingStonesSession? replacement, IReadOnlyList<CommunityLoginCookie> cookies,
        Func<SavedCommunityCookie[], Task>? save = null)
    {
        connection?.Dispose();
        connection = replacement is null ? null : new Connection(replacement, cookies, save);
    }

    internal async Task ResumeAsync(Uri? callback, int? code)
    {
        if (connection is not { } current)
            return;
        var usesTicket = callback is not null;
        current.Initialization = !usesTicket && current.FederatedCookies.Count == 0 ? Task.FromResult(false)
            : current.SignInAsync(callback ?? RisingStonesSession.Entry, usesTicket);
        if (!await current.Initialization)
            current.Session.RequireConfirmation(code);
    }

    internal async Task ResumeSavedAsync(IReadOnlyList<SavedCommunityCookie> cookies)
    {
        if (connection is not { } current)
            return;
        foreach (var cookie in cookies)
        {
            if (cookie.IsUsable(DateTimeOffset.UtcNow))
                current.AddCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.HttpOnly, cookie.Expires);
        }
        current.Initialization = cookies.Count == 0 ? Task.FromResult(false) : current.VerifyAsync(ticketOnly: true);
        if (!await current.Initialization)
            current.Session.RequireConfirmation();
    }

    internal async Task<string?> SelectRoleAsync(CommunityCharacter target, Func<bool> isCurrentCharacter)
    {
        if (connection is not { } current || !await current.Initialization
            || connection != current || !current.Session.CanSelectRole)
            return "石之家登录验证后才能切换角色。";
        if (current.SelectingRole || current.Session.Snapshot.JobsStatus == "loading")
            return "石之家正在读取角色信息。";
        if (current.Session.HasSelectedRole(target))
        {
            if (current.Session.Snapshot.JobsStatus != "ready")
                await current.ReadJobsAsync();
            return null;
        }

        bool CanContinue() => connection == current && current.Session.CanSelectRole && isCurrentCharacter();
        current.SelectingRole = true;
        try
        {
            var areas = await current.ReadApiAsync(RisingStonesSession.AreasUrl);
            if (!CanContinue())
                return "游戏或石之家会话已变化，请重新选择角色。";
            if (areas is null || RisingStonesSession.FindAreaId(areas, target.Area) is not { } areaId)
                return "无法在石之家核对所选角色的大区。";

            var rolesUrl = RisingStonesSession.CharactersUrl + "?AreaID=" + areaId.ToString(CultureInfo.InvariantCulture);
            var roles = await current.ReadApiAsync(rolesUrl);
            if (!CanContinue())
                return "游戏或石之家会话已变化，请重新选择角色。";
            if (roles is null || RisingStonesSession.FindCharacterId(roles, target) is not { } characterId)
                return "石之家角色列表中未找到同一大区和服务器的角色。";

            var reply = await current.ReadApiAsync(RisingStonesSession.SelectRoleUrl, new()
            {
                ["character_id"] = characterId,
                ["platform"] = "2",
                ["tempsuid"] = Guid.NewGuid().ToString("D")
            });
            if (!CanContinue())
                return "石之家会话已变化，请重新读取角色信息。";
            if (reply is null || !current.Session.ObserveRoleChange(reply, target))
                return "未能确认石之家角色切换。";

            await current.ReadJobsAsync();
            return null;
        }
        finally { current.SelectingRole = false; }
    }

    private sealed class Connection : IDisposable
    {
        private readonly CookieContainer cookies = new();
        private readonly HttpClient http;
        private readonly CancellationTokenSource cancellation = new();
        private readonly CancellationToken lifetime;
        private readonly Func<SavedCommunityCookie[], Task>? save;

        internal Connection(RisingStonesSession session, IReadOnlyList<CommunityLoginCookie> federatedCookies,
            Func<SavedCommunityCookie[], Task>? save)
        {
            Session = session;
            FederatedCookies = federatedCookies;
            this.save = save;
            lifetime = cancellation.Token;
            http = new HttpClient(new SocketsHttpHandler
            {
                CookieContainer = cookies,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 10) Lodestone/0.1");
        }

        internal RisingStonesSession Session { get; }
        internal IReadOnlyList<CommunityLoginCookie> FederatedCookies { get; }
        internal Task<bool> Initialization { get; set; } = Task.FromResult(false);
        internal bool SelectingRole { get; set; }

        internal void AddCookie(string name, string value, string domain, string path, bool httpOnly, DateTimeOffset? expires)
        {
            if (expires <= DateTimeOffset.UtcNow || !Uri.TryCreate("https://" + domain.TrimStart('.'), UriKind.Absolute, out var host))
                return;
            try
            {
                var cookie = new Cookie(name, value, path) { Secure = true, HttpOnly = httpOnly };
                if (expires is { } expiry)
                    cookie.Expires = expiry.UtcDateTime;
                if (domain.StartsWith('.'))
                    cookie.Domain = domain;
                cookies.Add(host, cookie);
            }
            catch (CookieException) { }
        }

        internal async Task<bool> SignInAsync(Uri entry, bool ticketOnly)
        {
            if (!ticketOnly)
            {
                foreach (var cookie in FederatedCookies)
                    AddCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.HttpOnly, cookie.Expires);
            }

            using var budget = StartRequest();
            try
            {
                var uri = entry;
                for (var redirects = 0; redirects < 8 && RisingStonesSession.IsAllowedPage(uri); redirects++)
                {
                    if (RisingStonesSession.IsPortal(uri))
                        return await VerifyAsync(ticketOnly);
                    if (ticketOnly && RisingStonesSession.IsPassportPage(uri))
                        return false;
                    using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                    if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } next)
                        break;
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                }
                return await VerifyAsync(ticketOnly);
            }
            catch (Exception ex) when (IsRequestError(ex)) { return false; }
        }

        internal async Task<bool> VerifyAsync(bool ticketOnly)
        {
            var status = await ReadApiAsync(RisingStonesSession.StatusUrl);
            if (status is null || lifetime.IsCancellationRequested)
                return false;
            Session.ObserveLogin(status, ticketOnly);
            if (!Session.CanSelectRole)
                return false;

            var bindings = await ReadApiAsync(RisingStonesSession.BindingsUrl);
            if (lifetime.IsCancellationRequested)
                return false;
            if (bindings is not null)
                Session.ObserveBindings(bindings);
            if (Session.Snapshot.State == "connected")
                await ReadJobsAsync();
            else
                await SaveCookiesAsync();
            return Session.CanSelectRole;
        }

        internal async Task ReadJobsAsync()
        {
            if (!Session.BeginJobRead())
                return;
            var data = await ReadApiAsync(RisingStonesSession.ProfileUrl);
            if (lifetime.IsCancellationRequested)
                return;
            if (data is null)
                Session.JobReadFailed();
            else
                Session.ObserveCurrentRole(data);
            await SaveCookiesAsync();
        }

        internal async Task<byte[]?> ReadApiAsync(string url, Dictionary<string, string>? form = null)
        {
            var uri = new Uri(url + (url.Contains('?') ? '&' : '?') + "tempsuid=" + Guid.NewGuid().ToString("D"));
            using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (form is not null)
                request.Content = new FormUrlEncodedContent(form);
            request.Headers.Add("Origin", RisingStonesSession.Portal.GetLeftPart(UriPartial.Authority));
            request.Headers.Referrer = new Uri(RisingStonesSession.Portal.GetLeftPart(UriPartial.Authority) + "/");

            using var budget = StartRequest();
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                    return null;
                using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
                var buffer = new byte[RisingStonesSession.MaxProfileBytes + 1];
                var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, budget.Token);
                return count == buffer.Length ? null : buffer[..count];
            }
            catch (Exception ex) when (IsRequestError(ex)) { return null; }
        }

        private async Task SaveCookiesAsync()
        {
            if (save is null || lifetime.IsCancellationRequested || !Session.CanSelectRole)
                return;
            var verified = new Dictionary<(string Host, string Path, string Name), SavedCommunityCookie>();
            foreach (var uri in new[] { new Uri(RisingStonesSession.StatusUrl), new Uri(RisingStonesSession.ProfileUrl), RisingStonesSession.Portal })
            {
                foreach (Cookie entry in cookies.GetCookies(uri))
                {
                    var cookie = new SavedCommunityCookie(entry.Name, entry.Value, uri.Host, entry.Path, entry.HttpOnly,
                        entry.Expires == DateTime.MinValue ? null : new DateTimeOffset(entry.Expires), 0);
                    if (entry.Secure && cookie.IsUsable(DateTimeOffset.UtcNow))
                        verified.TryAdd((cookie.Domain, cookie.Path, cookie.Name), cookie);
                }
            }
            await save(verified.Values.ToArray());
        }

        private CancellationTokenSource StartRequest()
        {
            var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            return budget;
        }

        private static bool IsRequestError(Exception ex) => ex is HttpRequestException or IOException
            or OperationCanceledException or ObjectDisposedException;

        public void Dispose()
        {
            cancellation.Cancel();
            http.Dispose();
            cancellation.Dispose();
        }
    }
}
