using static Lodestone.Core.Lobby.LobbyBinary;

namespace Lodestone.Core.Lobby;

internal sealed class LobbyConnection(Stream stream, TimeProvider clock)
{
    private const int BundleHeader = 40;
    private const int SegmentHeader = 16;
    private const int IpcHeader = 16;
    private const int MaximumBundle = 1 << 20;
    private const ushort Ipc = 3;
    private const ushort KeepAliveRequest = 7;
    private const ushort KeepAliveResponse = 8;
    private const ushort SecuritySetup = 9;
    private const ushort SecurityInitialize = 10;
    private static readonly byte[] IpcMagic = Convert.FromHexString("5252a041ff5d46e27f2a644d7b99c475");

    private readonly Queue<Segment> pending = new();
    private uint actor;
    private LobbyCipher? cipher;
    internal Dictionary<ushort, string> Worlds { get; } = [];

    internal async Task AuthenticateAsync(string sid, string versionInfo, ushort lobbyVersion, CancellationToken token)
    {
        var login = new byte[1456];
        Put(login, 0, 1u);
        Put(login, 8, (ushort)1);
        Put(login, 10, lobbyVersion);
        Put(login, 12, (ushort)0x1350);
        Put(login, 14, (ushort)24);
        WriteText(sid, login.AsSpan(16, 64), "SID");
        WriteText(versionInfo, login.AsSpan(400, 144), "version_info");
        try
        {
            await EstablishSecurityAsync(lobbyVersion, token);
            await SendIpcAsync(6, login, token);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(login);
        }
    }

    private async Task EstablishSecurityAsync(ushort lobbyVersion, CancellationToken token)
    {
        var first = await ReceiveAsync(token);
        if (first.Kind != KeepAliveRequest || first.Data.Length != 8 || !first.Retail)
            throw new LobbyException("Lobby 未返回支持的国服握手");

        var phrase = "fdbc958225e28b8d71a86233546c090f"u8;
        var time = UnixTime();
        var setup = new byte[616];
        phrase.CopyTo(setup.AsSpan(36, 32));
        Put(setup, 100, time);
        cipher = new LobbyCipher(phrase, time, lobbyVersion);
        await SendAsync(SecuritySetup, setup, token);

        while (true)
        {
            var segment = await ReceiveAsync(token);
            if (await ReplyToKeepAliveAsync(segment, token))
                continue;
            if (segment.Kind != SecurityInitialize || segment.Data.Length != 640)
                throw new LobbyException("Lobby 未返回预期的加密确认");
            cipher.Decrypt(segment.Data.AsSpan(0, 32));
            var id = U32(segment.Data, 0);
            if (id >> 24 != 0xe0 || U32(segment.Data, 8) != 0x40)
                throw new LobbyException("Lobby 加密确认失败，请核对 Lobby Version");
            actor = id;
            return;
        }
    }

    internal async Task SendIpcAsync(ushort opcode, byte[] payload, CancellationToken token)
    {
        var data = new byte[IpcHeader + payload.Length];
        Put(data, 0, (ushort)0x14);
        Put(data, 2, opcode);
        Put(data, 8, UnixTime());
        payload.CopyTo(data, IpcHeader);
        cipher!.Encrypt(data);
        await SendAsync(Ipc, data, token);
    }

    internal async Task<byte[]> ReceiveIpcAsync(ushort expected, CancellationToken token)
    {
        while (true)
        {
            var segment = await ReceiveAsync(token);
            if (await ReplyToKeepAliveAsync(segment, token) || segment.Kind != Ipc)
                continue;
            var data = segment.Data;
            cipher!.Decrypt(data);
            if (data.Length < IpcHeader || U16(data, 0) != 0x14)
                throw new LobbyException("Lobby IPC 校验失败");
            var opcode = U16(data, 2);
            var body = data[IpcHeader..];
            if (opcode == 2)
                throw new LobbyException(body.Length >= 18
                    ? $"Lobby 拒绝请求（错误码 {U32(body, 8)}）" : "Lobby 错误回包不完整");
            if (opcode == 21)
            {
                foreach (var (id, name) in LobbyRecords.WorldNames(body))
                    Worlds[id] = name;
            }
            if (opcode == expected)
                return body;
        }
    }

    private async Task<bool> ReplyToKeepAliveAsync(Segment segment, CancellationToken token)
    {
        if (segment.Kind != KeepAliveRequest)
            return false;
        if (segment.Data.Length != 8)
            throw new LobbyException("Lobby 心跳长度无效");
        Put(segment.Data, 4, UnixTime());
        await SendAsync(KeepAliveResponse, segment.Data, token);
        return true;
    }

    private async Task<Segment> ReceiveAsync(CancellationToken token)
    {
        while (pending.Count == 0)
        {
            var header = new byte[BundleHeader];
            await stream.ReadExactlyAsync(header, token);
            var size = U32(header, 24);
            var connection = U16(header, 28);
            var count = U16(header, 30);
            if (size is < BundleHeader or > MaximumBundle || connection is not (0 or 3))
                throw new LobbyException("Lobby 包头无效");
            if (header[33] != 0)
                throw new LobbyException("Lobby 返回了不支持的压缩包");
            var body = new byte[(int)size - BundleHeader];
            await stream.ReadExactlyAsync(body, token);
            var offset = 0;
            for (var index = 0; index < count; index++)
            {
                if (body.Length - offset < SegmentHeader)
                    throw new LobbyException("Lobby 数据段头不完整");
                var length = U32(body, offset);
                if (length < SegmentHeader || length > body.Length - offset)
                    throw new LobbyException("Lobby 数据段长度无效");
                var end = offset + (int)length;
                pending.Enqueue(new Segment(U16(body, offset + 12), body[(offset + SegmentHeader)..end],
                    connection == 0 && header[32] == 1));
                offset = end;
            }
            if (offset != body.Length)
                throw new LobbyException("Lobby 包与数据段的长度不匹配");
        }
        return pending.Dequeue();
    }

    private Task SendAsync(ushort kind, byte[] data, CancellationToken token)
    {
        var bundle = new byte[BundleHeader + SegmentHeader + data.Length];
        if (kind == Ipc)
        {
            IpcMagic.CopyTo(bundle, 0);
            Put(bundle, 16, (ulong)clock.GetUtcNow().ToUnixTimeMilliseconds());
            Put(bundle, BundleHeader + 4, actor);
            Put(bundle, BundleHeader + 8, actor);
        }
        Put(bundle, 24, (uint)bundle.Length);
        Put(bundle, 28, kind == SecuritySetup ? (ushort)3 : (ushort)0);
        Put(bundle, 30, (ushort)1);
        bundle[32] = kind is SecuritySetup or Ipc ? (byte)1 : (byte)0;
        Put(bundle, BundleHeader, (uint)(SegmentHeader + data.Length));
        Put(bundle, BundleHeader + 12, kind);
        data.CopyTo(bundle, BundleHeader + SegmentHeader);
        return stream.WriteAsync(bundle, token).AsTask();
    }

    private uint UnixTime() => (uint)clock.GetUtcNow().ToUnixTimeSeconds();
    private readonly record struct Segment(ushort Kind, byte[] Data, bool Retail);
}
