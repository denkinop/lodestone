using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Lodestone.Core.Lobby;

internal sealed class LobbyCipher
{
    private readonly BlowfishEngine encryption = new();
    private readonly BlowfishEngine decryption = new();

    internal LobbyCipher(ReadOnlySpan<byte> phrase, uint key, uint lobbyVersion)
    {
        Span<byte> material = stackalloc byte[12 + phrase.Length];
        LobbyBinary.Put(material, 0, 0x12345678u);
        LobbyBinary.Put(material, 4, key);
        LobbyBinary.Put(material, 8, lobbyVersion);
        phrase.CopyTo(material[12..]);
        var digest = MD5.HashData(material);

        // The protocol sign-extends each byte when constructing the Blowfish key words.
        for (var offset = 0; offset < digest.Length; offset += 4)
        {
            var word = 0;
            foreach (var value in digest.AsSpan(offset, 4))
            {
                var signedByte = value >= 128 ? 0xffffff00u | value : value;
                word = (word << 8) | unchecked((int)signedByte);
            }
            BinaryPrimitives.WriteInt32BigEndian(digest.AsSpan(offset), word);
        }

        var parameters = new KeyParameter(digest);
        encryption.Init(true, parameters);
        decryption.Init(false, parameters);
        CryptographicOperations.ZeroMemory(digest);
    }

    internal void Encrypt(Span<byte> bytes) => Transform(bytes, encryption);
    internal void Decrypt(Span<byte> bytes) => Transform(bytes, decryption);

    private static void Transform(Span<byte> bytes, BlowfishEngine engine)
    {
        var aligned = bytes.Length - bytes.Length % 32;
        for (var offset = 0; offset < aligned; offset += 8)
        {
            var block = bytes.Slice(offset, 8);
            block[..4].Reverse();
            block[4..].Reverse();
            engine.ProcessBlock(block, block);
            block[..4].Reverse();
            block[4..].Reverse();
        }
    }
}
