using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Lodestone.Core.Cdn;

internal sealed record CdnSigning(string Key, string Referer, string UserAgent)
{
    private static readonly BigInteger PublicModulus = new(Convert.FromHexString(
        "9b1f24d11fe0d6c3be6c8d18a3207a1b2dfdab30d137a1bdfa5aba5978f294f15f5e3d" +
        "094b5ef095090ab8b85485d8a0ed2709bd201a121f391adfa737f3231f624ffe14efee2" +
        "237163a1832cc66c36a31d0aadb0a4931aa096ba80dd26204375bfdf015caf513a136911" +
        "70a5cf2160b9fada23bcce1a5ebc06b8b568070cf17"), isUnsigned: true, isBigEndian: true);

    internal static CdnSigning Parse(byte[] bytes)
    {
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding("gb18030",
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;
        using var text = new StringReader(encoding.GetString(bytes).TrimStart('\uFEFF'));
        using var xml = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = OfficialCdnClient.MetadataLimit,
        });
        var game = XDocument.Load(xml).Root?.Elements("game")
            .FirstOrDefault(element => (string?)element.Attribute("gameid") == CdnManifest.GameId);
        if (game is null || (string?)game.Element("iplist")?.Attribute("type") != "2")
            throw new FormatException("官方 CDN 配置类型有变");

        var client = DecodeField(game.Element("client")?.Element("md5key"));
        var server = DecodeField(game.Element("server-let")?.Element("md5key"));
        var referer = DecodeField(game.Element("referer")?.Element("value2"));
        var userAgent = DecodeField(game.Element("md5key"));
        // The repair service uses opaque header values. In particular Referer is not a URL.
        if (client.Length < 16 || server.Length < 16
            || !IsHeaderValue(referer) || !IsHeaderValue(userAgent))
        {
            throw new FormatException("官方 CDN 下载配置字段无效");
        }
        return new CdnSigning(client[..16] + server[^16..], referer, userAgent);
    }

    private static bool IsHeaderValue(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(c => c is >= ' ' and <= '~');

    private static string DecodeField(XElement? element)
    {
        var signature = Convert.FromHexString(element?.Value.Trim() ?? "");
        if (signature.Length != 128)
            throw new FormatException("官方 CDN 配置签名长度无效");
        var number = new BigInteger(signature, isUnsigned: true, isBigEndian: true);
        if (number >= PublicModulus)
            throw new FormatException("官方 CDN 配置签名无效");

        var decoded = BigInteger.ModPow(number, 65537, PublicModulus).ToByteArray(isUnsigned: true, isBigEndian: true);
        // BigInteger omits the leading zero byte in the PKCS#1 type-1 block.
        if (decoded.Length != 127 || decoded[0] != 1)
            throw new FormatException("官方 CDN 配置签名校验失败");
        var separator = decoded.AsSpan(1).IndexOfAnyExcept((byte)0xff) + 1;
        if (separator < 9 || decoded[separator] != 0)
            throw new FormatException("官方 CDN 配置签名校验失败");

        return CdnManifest.Utf8.GetString(decoded.AsSpan(separator + 1));
    }

    internal Uri FileUri(CdnManifest manifest, CdnFile file, DateTimeOffset now)
    {
        var name = Convert.ToHexString(MD5.HashData(Encoding.Unicode.GetBytes(
            $"{CdnManifest.GameId}_{manifest.Revision}_{file.RelativePath}")));
        var segments = file.RelativePath.Replace('\\', '/').Split('/');
        var directory = string.Join('/', segments.SkipLast(1).Select(Uri.EscapeDataString));
        var path = manifest.BaseUri.AbsolutePath + (directory.Length == 0 ? "" : "/" + directory) + "/" + name;
        var timestamp = now.ToUnixTimeSeconds().ToString("x", CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(Key + path + timestamp)));
        return new Uri(manifest.BaseUri.GetLeftPart(UriPartial.Authority) + $"/{signature}/{timestamp}{path}");
    }
}
