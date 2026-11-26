using Lodestone.Core.Community;
using Xunit;

namespace Lodestone.Core.Tests;

public sealed class CommunityTests
{
    [Fact]
    public void CommunityBindingsRequireAMatchingOfficialAccount()
    {
        var session = new RisingStonesSession("9999999999001");
        var bindings = """{"data":[{"characterName":"测试角色","serverName":"示例世界"}]}"""u8.ToArray();
        session.ObserveBindings(bindings);
        Assert.Empty(session.Snapshot.Bindings);
        session.ObserveLogin("""{"data":{"isLogin":true,"sndaId":"9999999999002"}}"""u8.ToArray());
        Assert.Equal("identity", session.Snapshot.State);
        session.ObserveBindings(bindings);
        Assert.Empty(session.Snapshot.Bindings);

        session.ObserveLogin("""{"data":{"isLogin":true,"sndaId":"9999999999001"}}"""u8.ToArray());
        session.ObserveBindings(bindings);
        Assert.Equal("connected", session.Snapshot.State);
        Assert.Equal(new CommunityBinding("测试角色", "示例世界"), Assert.Single(session.Snapshot.Bindings));

        session.ObserveLogin("""{"data":false}"""u8.ToArray());
        Assert.Equal("expired", session.Snapshot.State);
        Assert.Empty(session.Snapshot.Bindings);
    }

    [Fact]
    public void CommunityConfirmationAndUnboundAccountAreDifferentStates()
    {
        var session = new RisingStonesSession("9999999999001");
        session.ObserveLogin("<html>verification needed</html>"u8.ToArray());
        Assert.Equal("confirmation", session.Snapshot.State);
        session.ObserveLogin("""{"data":{"isLogin":true,"sndaId":9999999999001}}"""u8.ToArray());
        session.ObserveBindings("""{"data":[]}"""u8.ToArray());
        Assert.Equal("unbound", session.Snapshot.State);
        session.Closed();
        Assert.Equal("confirmation", session.Snapshot.State);
    }

    [Fact]
    public void APositiveStatusWithoutAnIdentityRequiresTheTicketOnlyProfile()
    {
        var session = new RisingStonesSession("9999999999001", reusesSession: true);
        var positive = """{"data":true}"""u8.ToArray();
        session.ObserveLogin(positive);
        Assert.Equal("identity", session.Snapshot.State);
        session.ObserveLogin(positive, ticketOnlyProfile: true);
        Assert.Equal("connected", session.Snapshot.State);
        session.ObserveLogin("""{"data":{"isLogin":true,"sndaId":"another-account"}}"""u8.ToArray(), ticketOnlyProfile: true);
        Assert.Equal("identity", session.Snapshot.State);
        Assert.Empty(session.Snapshot.Bindings);
    }

    [Fact]
    public void OfficialPageCanDisplayTheCurrentRoleAfterTheCommunityAccountIsVerified()
    {
        var session = new RisingStonesSession("9999999999001");
        var role = """{"code":10000,"data":{"character_name":"测试角色","group_name":"示例世界"}}"""u8.ToArray();
        session.ObserveCurrentRole(role);
        Assert.Empty(session.Snapshot.Bindings);
        session.ObserveLogin("""{"data":{"isLogin":true,"sndaId":"9999999999001"}}"""u8.ToArray());
        session.ObserveCurrentRole(role);
        session.ObserveBindings("""{"data":[]}"""u8.ToArray());
        Assert.Equal(new CommunityBinding("测试角色", "示例世界"), Assert.Single(session.Snapshot.Bindings));
    }

    [Theory]
    [InlineData("{\"code\":10103,\"data\":{}}")]
    [InlineData("{\"code\":\"10104\"}")]
    public void OfficialRoleSelectionStatusKeepsTheVerifiedTicketSession(string response)
    {
        var session = new RisingStonesSession("9999999999001", reusesSession: true);
        var body = System.Text.Encoding.UTF8.GetBytes(response);
        session.ObserveLogin(body);
        Assert.Equal("identity", session.Snapshot.State);

        session.ObserveLogin(body, ticketOnlyProfile: true);
        Assert.Equal("unbound", session.Snapshot.State);
        Assert.Empty(session.Snapshot.Bindings);

        session.ObserveLogin("""{"code":10103,"data":{"isLogin":false}}"""u8.ToArray(), ticketOnlyProfile: true);
        Assert.Equal("expired", session.Snapshot.State);
    }

    [Fact]
    public void OfficialBindingResponseProvidesTheRoleNameAndWorld()
    {
        var session = new RisingStonesSession("9999999999001", reusesSession: true);
        session.ObserveLogin("""{"code":10000,"data":{"sndaId":"another-account"}}"""u8.ToArray(), ticketOnlyProfile: true);
        Assert.Equal("identity", session.Snapshot.State);

        session.ObserveLogin("""{"code":10000,"data":{}}"""u8.ToArray(), ticketOnlyProfile: true);
        session.ObserveBindings("""{"code":10000,"data":{"character_name":"测试角色","group_name":"示例世界"}}"""u8.ToArray());
        Assert.Equal("connected", session.Snapshot.State);
        Assert.Equal(new CommunityBinding("测试角色", "示例世界"), Assert.Single(session.Snapshot.Bindings));

        session.ObserveBindings("""{"code":10103,"data":{}}"""u8.ToArray());
        Assert.Equal("unbound", session.Snapshot.State);
        Assert.Empty(session.Snapshot.Bindings);
    }

    [Fact]
    public void ReadsOfficialCharacterCareerLevelsAfterVerifyingTheCommunitySession()
    {
        var session = new RisingStonesSession("fixture", reusesSession: true);
        var profile = """
            {"code":10000,"data":{"character_name":"测试角色","area_name":"示例大区","group_name":"示例世界",
              "careerLevel":[{"career":"学者","character_level":"91","career_type":"战斗精英"},
                {"career":"刻木匠","character_level":100},{"career":"采矿工","character_level":"0"},
                {"career":"绝枪战士","character_level":"******"}]}}
            """u8.ToArray();

        Assert.False(session.BeginJobRead());
        session.ObserveCurrentRole(profile);
        Assert.Null(session.Snapshot.JobProfile);
        session.ObserveLogin("""{"code":10000,"data":{}}"""u8.ToArray(), ticketOnlyProfile: true);
        Assert.True(session.BeginJobRead());
        Assert.False(session.BeginJobRead());
        session.ObserveCurrentRole(profile);

        Assert.Equal("ready", session.Snapshot.JobsStatus);
        var jobs = Assert.IsType<CommunityJobProfile>(session.Snapshot.JobProfile);
        Assert.Equal(("测试角色", "示例大区", "示例世界"), (jobs.Character, jobs.Area, jobs.World));
        Assert.Equal(3, jobs.Levels.Count);
        Assert.Equal(91, jobs.Levels["学者"]);
        Assert.Equal(100, jobs.Levels["刻木匠"]);
        Assert.Equal(0, jobs.Levels["采矿工"]);

        session.ObserveCurrentRole("""{"code":10403,"msg":"请先登录"}"""u8.ToArray());
        Assert.Equal("expired", session.Snapshot.State);
        Assert.Null(session.Snapshot.JobProfile);
    }

    [Fact]
    public void DoesNotAttachACommunityProfileToADifferentBinding()
    {
        var session = new RisingStonesSession("fixture", reusesSession: true);
        session.ObserveLogin("""{"code":10000,"data":{}}"""u8.ToArray(), ticketOnlyProfile: true);
        session.ObserveBindings("""{"code":10000,"data":{"character_name":"测试角色","group_name":"示例世界"}}"""u8.ToArray());
        Assert.True(session.BeginJobRead());
        session.ObserveCurrentRole("""
            {"code":10000,"data":{"character_name":"测试角色","area_name":"示例大区","group_name":"另一个世界",
            "careerLevel":[{"career":"学者","character_level":"91"}]}}
            """u8.ToArray());
        Assert.Null(session.Snapshot.JobProfile);
        Assert.Equal("unavailable", session.Snapshot.JobsStatus);

        Assert.True(session.BeginJobRead());
        session.ObserveCurrentRole("""{"code":10103,"data":{}}"""u8.ToArray());
        Assert.Equal("unbound", session.Snapshot.State);
        Assert.Null(session.Snapshot.JobProfile);
    }

    [Theory]
    [InlineData(401, null)]
    [InlineData(200, "{\"code\":\"10403\",\"msg\":\"请先登录\"}")]
    [InlineData(403, "{\"code\":10403}")]
    public void ExplicitAuthenticationFailuresExpireASavedOrVerifiedCommunityLogin(int status, string? reply)
    {
        var body = reply is null ? null : System.Text.Encoding.UTF8.GetBytes(reply);
        var initial = new RisingStonesSession("fixture", reusesSession: true);
        Assert.True(initial.ObserveAuthenticationFailure(status, body));
        Assert.Equal("confirmation", initial.Snapshot.State);

        var saved = new RisingStonesSession("fixture", reusesSession: true, restoresSavedSession: true);
        Assert.True(saved.ObserveAuthenticationFailure(status, body));
        Assert.Equal("expired", saved.Snapshot.State);

        var verified = new RisingStonesSession("fixture");
        verified.ObserveLogin("""{"data":{"isLogin":true,"sndaId":"fixture"}}"""u8.ToArray());
        Assert.True(verified.ObserveAuthenticationFailure(status, body));
        verified.ObserveLogin("""{"data":{"isLogin":true,"sndaId":"fixture"}}"""u8.ToArray());
        verified.Closed();
        Assert.Equal("expired", verified.Snapshot.State);
        Assert.False(verified.CanSelectRole);
    }

    [Fact]
    public void SavedCommunityLoginDistinguishesSignedOutStatusFromAnUnreadableResponse()
    {
        var session = new RisingStonesSession("fixture", restoresSavedSession: true);
        Assert.False(session.ObserveAuthenticationFailure(503, null));
        Assert.False(session.ObserveAuthenticationFailure(403, """{"code":99999}"""u8.ToArray()));
        Assert.False(session.ObserveAuthenticationFailure(200, "<html>maintenance</html>"u8.ToArray()));
        session.ObserveLogin("<html>maintenance</html>"u8.ToArray());
        Assert.Equal("confirmation", session.Snapshot.State);

        session.ObserveLogin("""{"code":10000,"data":{"isLogin":false}}"""u8.ToArray());
        session.RequireConfirmation();
        Assert.Equal("expired", session.Snapshot.State);
    }

    [Fact]
    public void UnavailableJobsCanConfirmExpiryWithoutDiscardingAValidSessionOnAnAmbiguousReply()
    {
        var session = new RisingStonesSession("fixture");
        session.ObserveLogin("""{"data":{"isLogin":true,"sndaId":"fixture"}}"""u8.ToArray());
        Assert.True(session.BeginJobRead());
        session.ObserveCurrentRole("""{"code":10000,"data":{}}"""u8.ToArray());

        session.ObserveLoginCheck("<html>maintenance</html>"u8.ToArray());
        session.ObserveLoginCheck("""{"code":10000,"data":{"isLogin":true}}"""u8.ToArray());
        session.ObserveLoginCheck("""{"code":99999,"data":false}"""u8.ToArray());
        Assert.Equal("connected", session.Snapshot.State);
        Assert.Equal("unavailable", session.Snapshot.JobsStatus);

        session.ObserveLoginCheck("""{"code":10000,"data":{"isLogin":false}}"""u8.ToArray());
        Assert.Equal("expired", session.Snapshot.State);
        Assert.False(session.CanSelectRole);
    }

    [Fact]
    public void ARejectedRoleChangeRequiresNewAuthorization()
    {
        var session = new RisingStonesSession("fixture");
        session.ObserveLogin("""{"data":{"isLogin":true,"sndaId":"fixture"}}"""u8.ToArray());
        Assert.False(session.ObserveRoleChange("""{"code":10403}"""u8.ToArray(), new("测试角色", "示例大区", "示例世界")));
        Assert.Equal("expired", session.Snapshot.State);
    }

    [Fact]
    public void ResolvesTheSelectedRosterCharacterUsingTheCommunityIdWithoutLosingPrecision()
    {
        var areas = """{"code":10000,"data":[{"AreaID":"6","AreaName":"示例大区"},{"AreaID":2,"AreaName":"另一个大区"}]}"""u8.ToArray();
        var roster = """
            {"code":10000,"data":[{"character_name":"测试角色","group_name":"示例世界","character_id":9007199254740997},
            {"character_name":"测试角色","group_name":"另一个世界","character_id":"9007199254740999"}]}
            """u8.ToArray();
        var target = new CommunityCharacter("测试角色", "示例大区", "示例世界");
        Assert.Equal(6, RisingStonesSession.FindAreaId(areas, target.Area));
        Assert.Equal("9007199254740997", RisingStonesSession.FindCharacterId(roster, target));
        Assert.Equal("9007199254740999", RisingStonesSession.FindCharacterId(roster, target with { World = "另一个世界" }));
        Assert.Null(RisingStonesSession.FindCharacterId(roster, target with { World = "未知世界" }));
    }

    [Fact]
    public void AConfirmedRoleChangeReplacesThePreviousLevelsAndRejectsStaleProfiles()
    {
        var session = new RisingStonesSession("fixture", reusesSession: true);
        var target = new CommunityCharacter("同名角色", "示例大区", "示例世界");
        var success = """{"code":10002,"data":{}}"""u8.ToArray();
        var previous = """
            {"code":10000,"data":{"character_name":"同名角色","area_name":"另一个大区","group_name":"示例世界",
            "careerLevel":[{"career":"学者","character_level":"19"}]}}
            """u8.ToArray();
        Assert.False(session.ObserveRoleChange(success, target));
        session.ObserveLogin("""{"code":10000,"data":{}}"""u8.ToArray(), ticketOnlyProfile: true);
        session.ObserveCurrentRole(previous);
        Assert.NotNull(session.Snapshot.JobProfile);
        Assert.False(session.ObserveRoleChange("""{"code":99999}"""u8.ToArray(), target));
        Assert.True(session.ObserveRoleChange(success, target));
        Assert.True(session.HasSelectedRole(target));
        Assert.Null(session.Snapshot.JobProfile);
        session.ObserveBindings("""{"code":10000,"data":{"character_name":"无关角色","group_name":"示例世界"}}"""u8.ToArray());
        Assert.Equal(target.Character, Assert.Single(session.Snapshot.Bindings).Character);
        session.ObserveCurrentRole(previous);
        Assert.Null(session.Snapshot.JobProfile);
        session.ObserveCurrentRole("""
            {"code":10000,"data":{"character_name":"同名角色","area_name":"示例大区","group_name":"示例世界",
            "careerLevel":[{"career":"学者","character_level":"99"}]}}
            """u8.ToArray());
        Assert.Equal(99, Assert.IsType<CommunityJobProfile>(session.Snapshot.JobProfile).Levels["学者"]);
    }

    [Theory]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2", "GET", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=1", "GET", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=3", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2", "POST", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2", "OPTIONS", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2&tempsuid=fixture", "GET", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2&platform=2", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/characterBind", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getAreaAndGroupList", "GET", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getAreaAndGroupList", "POST", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getFF14Characters?AreaID=6", "GET", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getFF14Characters?AreaID=6&tempsuid=fixture", "OPTIONS", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getFF14Characters?AreaID=6", "POST", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getFF14Characters", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getFF14Characters?AreaID=0", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getFF14Characters?AreaID=6&AreaID=7", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getFF14Characters?AreaID=6&uuid=123", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com.example.org/api/home/groupAndRole/getFF14Characters?AreaID=6", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/bindCharacterInfo", "POST", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/bindCharacterInfo?tempsuid=fixture", "POST", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/bindCharacterInfo", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/userInfo/getUserInfo", "GET", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/userInfo/getUserInfo?tempsuid=fixture", "GET", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/userInfo/getUserInfo?tempsuid=fixture", "POST", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/userInfo/getUserInfo?uuid=123", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com.example.org/api/home/GHome/isLogin", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/GHome/login", "GET", true)]
    public void CommunityApiAccessIsLimitedToTheKnownReadAndLoginEndpoints(string uri, string method, bool result) =>
        Assert.Equal(result, new RisingStonesSession("fixture").AllowsApiRequest(new Uri(uri), method));

    [Fact]
    public void SavedCommunitySessionCanReadTheProfileWithoutOpeningSignIn()
    {
        var session = new RisingStonesSession("fixture");
        Assert.False(session.AllowsApiRequest(RisingStonesSession.Entry, "GET", allowSignIn: false));
        Assert.False(session.AllowsApiRequest(new Uri("https://login.u.sdo.com/sdo/Login/LoginSDO.php"), "GET", allowSignIn: false));
        Assert.True(session.AllowsApiRequest(new Uri(RisingStonesSession.StatusUrl), "GET", allowSignIn: false));
        Assert.True(session.AllowsApiRequest(new Uri(RisingStonesSession.ProfileUrl), "GET", allowSignIn: false));
    }
}
