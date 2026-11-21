using System.Buffers.Binary;
using System.Text;

namespace Lodestone.Core.Lobby;

public sealed class LobbyException(string message, Exception? inner = null) : IOException(message, inner);

internal static class LobbyBinary
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    internal static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    internal static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    internal static void Put(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], value);
    internal static void Put(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    internal static void Put(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);

    internal static ReadOnlySpan<byte> UntilNull(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return end < 0 ? bytes : bytes[..end];
    }

    internal static string Text(ReadOnlySpan<byte> bytes)
    {
        try { return Utf8.GetString(UntilNull(bytes)); }
        catch (DecoderFallbackException) { throw new LobbyException("Lobby 文本编码无效"); }
    }

    internal static void WriteText(string text, Span<byte> destination, string field)
    {
        if (text.Length == 0 || text.Length > destination.Length || text.Any(c => c is < '!' or > '~'))
            throw new LobbyException($"{field} 字段的长度或格式无效");
        Encoding.ASCII.GetBytes(text, destination);
    }
}
