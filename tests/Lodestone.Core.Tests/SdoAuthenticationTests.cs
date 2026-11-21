using System.Net;
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
        Assert.Empty(result.CommunityCookies);
        Assert.Equal(["getPromotionInfo.json", "ssoLogin.json"], transport.Endpoints.TakeLast(2));
        Assert.DoesNotContain("fake-sid", result.ToString());
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
        internal bool Reject { get; init; }
        internal bool IssueCommunitySession { get; init; }

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
            if (endpoint is "getPromotionInfo.json" or "ssoLogin.json")
                Assert.Equal("CASTGC=fake-tgt; CAS_LOGIN_STATE=1", Assert.Single(request.Headers.GetValues("Cookie")));
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
                "pushMessageLogin.json" or "codeKeyLogin.json" =>
                    """{"return_code":0,"data":{"tgt":"fake-tgt","sndaId":"99999999999992","nextAction":0}}""",
                "ssoLogin.json" => """{"return_code":0,"data":{"ticket":"fake-sid"}}""",
                _ => """{"return_code":0}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
