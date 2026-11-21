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
        Assert.Equal("expired", session.Snapshot.State);
    }

    [Theory]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2", "GET", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=3", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2", "POST", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/getCharacterBindInfo?platform=2", "OPTIONS", true)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/groupAndRole/characterBind", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com.example.org/api/home/GHome/isLogin", "GET", false)]
    [InlineData("https://apiff14risingstones.web.sdo.com/api/home/GHome/login", "GET", true)]
    public void CommunityApiAccessIsLimitedToTheKnownReadAndLoginEndpoints(string uri, string method, bool result) =>
        Assert.Equal(result, new RisingStonesSession("fixture").AllowsApiRequest(new Uri(uri), method));
}
