using System.Security.Cryptography;
using System.Text;

namespace Lodestone.Core.Auth;

internal sealed record SdoIdentity(string Device, string Mac, string Host, string ClientId)
{
    internal static SdoIdentity FromInstallation(Guid installation)
    {
        var seed = installation.ToString("N");
        var macBytes = SHA256.HashData(Encoding.ASCII.GetBytes(seed));
        macBytes[0] = (byte)((macBytes[0] | 2) & ~1);
        var mac = BitConverter.ToString(macBytes, 0, 6);
        var macHash = Md5(mac);
        return new SdoIdentity(macHash + ":" + Md5(seed + "cpu") + ":" + Md5(seed + "disk"),
            mac, "Lodestone-" + seed[..8], "CID" + macHash);
    }

    private static string Md5(string value) => Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(value)));
}
