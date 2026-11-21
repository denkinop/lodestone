using System.Net;
using System.Text.Json;
using Lodestone.Core.Auth;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class SdoAuthenticationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExchangesConfirmedAuthorizationForGameTicket(bool scan)
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies);
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var challenges = new List<LoginChallenge>();
        var result = scan
            ? await auth.ConfirmByScanAsync(challenges.Add, CancellationToken.None)
            : await auth.ConfirmByPhoneAsync("test+account@example.org", challenges.Add, CancellationToken.None);

        if (scan)
            Assert.IsType<ScanConfirmation>(Assert.Single(challenges));
        else
            Assert.Equal("152", Assert.IsType<PhoneConfirmation>(Assert.Single(challenges)).Number);
        Assert.Equal("fake-sid", result.Sid);
        Assert.Equal("99999999999992", result.AccountId);
        Assert.Null(result.SavedLogin);
        Assert.Empty(result.CommunityCookies);
        Assert.Equal(["getPromotionInfo.json", "ssoLogin.json"], transport.Endpoints.TakeLast(2));
        Assert.DoesNotContain("fake-sid", result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RememberingAnAuthorizedLoginUsesTheSdoSessionKey(bool scan)
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies) { Remember = true };
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var result = scan
            ? await auth.ConfirmByScanAsync(_ => { }, CancellationToken.None, remember: true)
            : await auth.ConfirmByPhoneAsync("test+account@example.org", _ => { }, CancellationToken.None, remember: true);

        var saved = Assert.IsType<SavedGameLogin>(result.SavedLogin);
        Assert.Equal(result.AccountId, saved.AccountId);
        Assert.Equal(scan ? "scan+key" : "phone+key", saved.SessionKey);
        Assert.Equal(scan, transport.Endpoints.Contains("accountGroupLogin"));
        Assert.DoesNotContain(saved.SessionKey, JsonSerializer.Serialize(saved));
        Assert.DoesNotContain(saved.SessionKey, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ScanLoginContinuesIfTheOptionalSavedKeyIsUnavailable()
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies) { Remember = true, RejectGroup = true };
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var result = await auth.ConfirmByScanAsync(_ => { }, CancellationToken.None, remember: true);
        Assert.Null(result.SavedLogin);
        Assert.Equal("fake-sid", result.Sid);
    }

    [Fact]
    public async Task RestoringAnAccountRotatesTheKeyBeforeCompletingGameLogin()
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies);
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var result = await auth.RestoreAsync(new SavedGameLogin("99999999999992", "old+key&fixture"), rotated =>
        {
            Assert.Equal("rotated+key", rotated.SessionKey);
            Assert.Equal(["getGuid.json", "autoLogin.json"], transport.Endpoints);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal("fake-sid", result.Sid);
        Assert.Equal("99999999999992", result.AccountId);
        Assert.Equal(["getGuid.json", "autoLogin.json", "fastLogin.json", "getPromotionInfo.json", "ssoLogin.json"], transport.Endpoints);
        Assert.NotNull((await result.ExchangeForCommunityAsync(CancellationToken.None)).TakeCallback());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-account")]
    [InlineData("fast-wrong-account")]
    public async Task SavedLoginRejectsExpiredOrMismatchedAccounts(string refusal)
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies) { SavedRefusal = refusal };
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var refreshed = false;
        await Assert.ThrowsAsync<SavedGameLoginRejectedException>(() =>
            auth.RestoreAsync(new SavedGameLogin("99999999999992", "old+key&fixture"), _ =>
            {
                refreshed = true;
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.Equal(refusal == "fast-wrong-account", refreshed);
        Assert.DoesNotContain("getPromotionInfo.json", transport.Endpoints);
        Assert.DoesNotContain("ssoLogin.json", transport.Endpoints);
    }

    [Fact]
    public async Task SharesOnlyTheIssuerScopedSecureCommunitySession()
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies) { IssueCommunitySession = true };
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var result = await auth.ConfirmByPhoneAsync("test+account@example.org", _ => { }, CancellationToken.None);

        var cookie = Assert.Single(result.CommunityCookies);
        Assert.Equal("CASTGC", cookie.Name);
        Assert.Equal(".sdo.com", cookie.Domain);
        Assert.Equal("fake-community-cookie", cookie.Value);
        Assert.True(cookie.HttpOnly);
        Assert.DoesNotContain("fake-community-cookie", cookie.ToString());
        Assert.DoesNotContain("fake-community-cookie", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("fake-community-cookie", JsonSerializer.Serialize(cookie));
    }

    [Fact]
    public async Task UsesTheSameAuthorizedContextToIssueASeparateCommunityTicketOnce()
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies);
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var result = await auth.ConfirmByPhoneAsync("test+account@example.org", _ => { }, CancellationToken.None);
        var issued = await result.ExchangeForCommunityAsync(CancellationToken.None);

        var callback = Assert.IsType<Uri>(issued.TakeCallback());
        Assert.Equal("apiff14risingstones.web.sdo.com", callback.Host);
        Assert.Equal("/api/home/GHome/login", callback.AbsolutePath);
        Assert.Contains("ticket=community%2Bticket%26test", callback.Query);
        Assert.DoesNotContain("fake-sid", callback.AbsoluteUri);
        Assert.DoesNotContain("fake-tgt", callback.AbsoluteUri);
        Assert.Null(issued.TakeCallback());
        Assert.Null((await result.ExchangeForCommunityAsync(CancellationToken.None)).TakeCallback());
        Assert.Equal(["getPromotionInfo.json", "ssoLogin.json"], transport.CommunityEndpoints);
        Assert.Equal("fake-sid", result.Sid);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("denied")]
    [InlineData("game-ticket")]
    [InlineData("other-account")]
    [InlineData("user-action")]
    public async Task CommunityCanRequireConfirmationWithoutInvalidatingGameLogin(string refusal)
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies) { Refusal = refusal };
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var result = await auth.ConfirmByPhoneAsync("test+account@example.org", _ => { }, CancellationToken.None);

        var issued = await result.ExchangeForCommunityAsync(CancellationToken.None);

        Assert.Null(issued.TakeCallback());
        Assert.Equal("fake-sid", result.Sid);
        if (refusal == "denied")
            Assert.Equal(-10842112, issued.Code);
        Assert.Equal(refusal == "approval" ? 1 : 2, transport.CommunityEndpoints.Count);
    }

    [Fact]
    public async Task ACancelledCommunityExchangeDoesNotConsumeTheGameTicket()
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies);
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);
        var result = await auth.ConfirmByPhoneAsync("test+account@example.org", _ => { }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.ExchangeForCommunityAsync(cancellation.Token));
        Assert.Equal("fake-sid", result.Sid);
        Assert.Empty(transport.CommunityEndpoints);
    }

    [Fact]
    public async Task RemoteFailureDoesNotLeakRemoteDiagnosticOrAttemptTicketExchange()
    {
        var cookies = new CookieContainer();
        using var transport = new CasTransport(cookies) { Reject = true };
        using var auth = new SdoAuthenticator(Guid.NewGuid(), cookies, transport);

        var error = await Assert.ThrowsAsync<SdoAuthException>(() =>
            auth.ConfirmByPhoneAsync("test+account@example.org", _ => { }, CancellationToken.None));

        Assert.DoesNotContain("remote-sensitive-data", error.ToString());
        Assert.DoesNotContain("ssoLogin.json", transport.Endpoints);
    }

    private sealed class CasTransport(CookieContainer cookies) : HttpMessageHandler
    {
        internal List<string> Endpoints { get; } = [];
        internal List<string> CommunityEndpoints { get; } = [];
        internal bool Reject { get; init; }
        internal bool Remember { get; init; }
        internal bool RejectGroup { get; init; }
        internal string? SavedRefusal { get; init; }
        internal bool IssueCommunitySession { get; init; }
        internal string? Refusal { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Assert.Equal("cas.sdo.com", uri.Host);
            Assert.Equal("https", uri.Scheme);
            var endpoint = uri.Segments[^1];
            Endpoints.Add(endpoint);
            if (endpoint == "getCodeKey.json")
            {
                cookies.Add(uri, new Cookie("CODEKEY", "temporary-code", "/authen") { Secure = true });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([137, 80, 78, 71, 13, 10, 26, 10, 0])
                });
            }
            if (endpoint == "sendPushMessage.json")
                Assert.Contains("inputUserId=test%2Baccount%40example.org", uri.Query);
            if (endpoint == "codeKeyLogin.json")
                Assert.Contains("codeKey=temporary-code", uri.Query);
            if (endpoint is "codeKeyLogin.json" or "pushMessageLogin.json")
                Assert.Equal(Remember, uri.Query.Contains("autoLoginFlag=1&autoLoginKeepTime=30"));
            if (endpoint == "accountGroupLogin")
            {
                Assert.Contains("serviceUrl=http%3A%2F%2Fwww.sdo.com", uri.Query);
                Assert.Contains("sndaId=99999999999992", uri.Query);
                Assert.Contains("autoLoginFlag=1&autoLoginKeepTime=30", uri.Query);
            }
            if (endpoint == "autoLogin.json")
                Assert.Contains("autoLoginSessionKey=old%2Bkey%26fixture", uri.Query);
            if (endpoint == "fastLogin.json")
                Assert.Contains("tgt=refreshed-tgt&guid=fake-guid", uri.Query);
            if (endpoint is "getPromotionInfo.json" or "ssoLogin.json")
                Assert.Equal(Endpoints.Contains("accountGroupLogin") && !RejectGroup ? "CASTGC=scan-tgt; CAS_LOGIN_STATE=1" :
                    "CASTGC=fake-tgt; CAS_LOGIN_STATE=1", Assert.Single(request.Headers.GetValues("Cookie")));
            if (uri.Query.Contains("appId=6788", StringComparison.Ordinal))
            {
                CommunityEndpoints.Add(endpoint);
                var fields = uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
                Assert.Equal("6788", fields["appId"]);
                Assert.Equal("1", fields["areaId"]);
                Assert.Equal("2", fields["authenSource"]);
                Assert.Equal("fake-guid", fields["guid"]);
                Assert.Equal("fake-tgt", fields["tgt"]);
                Assert.Equal("https://apiff14risingstones.web.sdo.com/api/home/GHome/login", fields["serviceUrl"]);
                return Task.FromResult(Reply((endpoint, Refusal) switch
                {
                    ("getPromotionInfo.json", "approval") =>
                        """{"return_code":0,"data":{"promotionUrl":"https://example.org/approval"}}""",
                    ("getPromotionInfo.json", _) => """{"return_code":0,"data":{}}""",
                    ("ssoLogin.json", "denied") => """{"return_code":-10842112,"data":{}}""",
                    ("ssoLogin.json", "game-ticket") => """{"return_code":0,"data":{"ticket":"fake-sid"}}""",
                    ("ssoLogin.json", "other-account") =>
                        """{"return_code":0,"data":{"sndaId":"another-account","ticket":"a-ticket"}}""",
                    ("ssoLogin.json", "user-action") =>
                        """{"return_code":0,"data":{"nextAction":1,"ticket":"a-ticket"}}""",
                    ("ssoLogin.json", _) =>
                        """{"return_code":0,"data":{"nextAction":0,"ticket":"community+ticket&test"}}""",
                    _ => throw new InvalidOperationException()
                }));
            }
            if (endpoint == "ssoLogin.json" && IssueCommunitySession)
            {
                cookies.Add(new Cookie("CASTGC", "fake-community-cookie", "/", ".sdo.com") { Secure = true, HttpOnly = true });
                cookies.Add(new Cookie("CAS_LOGIN_STATE", "1", "/authen", "cas.sdo.com") { Secure = true });
                cookies.Add(new Cookie("other", "unused", "/", ".sdo.com") { Secure = true });
            }
            var json = endpoint switch
            {
                "getGuid.json" => """{"return_code":0,"data":{"guid":"fake-guid"}}""",
                "sendPushMessage.json" when Reject => """{"return_code":13,"data":{"failReason":"remote-sensitive-data"}}""",
                "sendPushMessage.json" => """{"return_code":0,"data":{"pushMsgSerialNum":"152","pushMsgSessionKey":"fake-confirm"}}""",
                "accountGroupLogin" when RejectGroup => """{"return_code":-10515005}""",
                "accountGroupLogin" => """{"return_code":0,"data":{"tgt":"scan-tgt","autoLoginSessionKey":"scan+key"}}""",
                "autoLogin.json" when SavedRefusal == "expired" => """{"return_code":-10515005}""",
                "autoLogin.json" when SavedRefusal == "wrong-account" =>
                    """{"return_code":0,"data":{"tgt":"refreshed-tgt","sndaId":"other","autoLoginSessionKey":"rotated+key"}}""",
                "autoLogin.json" =>
                    """{"return_code":0,"data":{"tgt":"refreshed-tgt","sndaId":"99999999999992","autoLoginSessionKey":"rotated+key"}}""",
                "fastLogin.json" when SavedRefusal == "fast-wrong-account" =>
                    """{"return_code":0,"data":{"tgt":"fake-tgt","sndaId":"other"}}""",
                "fastLogin.json" => """{"return_code":0,"data":{"tgt":"fake-tgt","sndaId":"99999999999992"}}""",
                "pushMessageLogin.json" when Remember =>
                    """{"return_code":0,"data":{"tgt":"fake-tgt","sndaId":"99999999999992","autoLoginSessionKey":"phone+key"}}""",
                "pushMessageLogin.json" or "codeKeyLogin.json" =>
                    """{"return_code":0,"data":{"tgt":"fake-tgt","sndaId":"99999999999992","nextAction":0}}""",
                "ssoLogin.json" => """{"return_code":0,"data":{"ticket":"fake-sid"}}""",
                _ => """{"return_code":0}"""
            };
            return Task.FromResult(Reply(json));
        }

        private static HttpResponseMessage Reply(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
