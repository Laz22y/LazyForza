using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace LazyForza.EstatePeer;

/// <summary>Authenticated, replay-protected discovery between the admitted peers only.</summary>
internal sealed class PeerUdpSession
{
    internal const int HeaderBytes = 30;
    internal const int TagBytes = 16;
    internal const int LargeProbeBytes = 1232;
    internal const byte Challenge = 3, Response = 4, SizeProbe = 5, SizeResponse = 6;
    private readonly object sync = new();
    private readonly byte[] key;
    private readonly byte[] identifier;
    private readonly byte role;
    private readonly IReadOnlyList<PeerEndpoint> candidates;
    private readonly Func<long> clock;
    private readonly Dictionary<IPEndPoint, (long Time, long LastSent, byte[] Token)> challenges = [];
    private readonly Dictionary<IPEndPoint, long> validated = [];
    private TaskCompletionSource ready = NewCompletion();
    private IPEndPoint? selected;
    private byte[]? sizeToken;
    private long lastSizeProbe;
    private long lastSizeConfirmed = long.MinValue / 2;
    private int fragmentBytes = 512;
    private long sendSequence;
    private ulong highestReceived, receivedMask;
    internal string Identifier => Convert.ToHexString(identifier);
    internal IPEndPoint? Selected { get { lock (sync) return selected; } }
    internal int FragmentBytes { get { lock (sync) return fragmentBytes; } }

    internal PeerUdpSession(PeerReceipt receipt, IReadOnlyList<PeerEndpoint> candidates, bool host, Func<long>? clock = null)
    {
        key = PeerPathAuthentication.Key(receipt, "udp-v2");
        identifier = PeerPathAuthentication.Identifier(receipt);
        this.candidates = candidates;
        role = host ? (byte)1 : (byte)0;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    internal Task BeginAttempt()
    {
        lock (sync)
        {
            ready = NewCompletion();
            challenges.Clear();
            // A previous small/large-packet success is not evidence for this attempt.
            fragmentBytes = 512; sizeToken = null; lastSizeProbe = 0; lastSizeConfirmed = long.MinValue / 2;
            return ready.Task;
        }
    }

    internal IReadOnlyList<(IPEndPoint Remote, byte[] Bytes)> Probe()
    {
        lock (sync)
        {
            var outgoing = new List<(IPEndPoint, byte[])>();
            var endpoints = validated.OrderByDescending(item => item.Value).Select(item => item.Key)
                .Concat(candidates.Select(item => new IPEndPoint(item.Address, item.Port)));
            if (selected is not null) endpoints = endpoints.Prepend(selected);
            foreach (var endpoint in endpoints.Distinct().Take(9)) AddChallenge(endpoint, outgoing);
            if (sizeToken is not null && lastSizeConfirmed < lastSizeProbe && clock() - lastSizeProbe > 3000)
                fragmentBytes = 512;
            if (selected is not null && (sizeToken is null || clock() - lastSizeProbe >= 10_000))
            {
                sizeToken = RandomNumberGenerator.GetBytes(16); lastSizeProbe = clock();
                var payload = new byte[LargeProbeBytes - HeaderBytes - TagBytes];
                sizeToken.CopyTo(payload, 0);
                outgoing.Add((selected, Encode(SizeProbe, payload)));
            }
            return outgoing;
        }
    }

    internal (byte Kind, byte[] Payload)? Receive(byte[] packet, IPEndPoint remote,
        out IReadOnlyList<(IPEndPoint Remote, byte[] Bytes)> responses)
    {
        lock (sync)
        {
            var outgoing = new List<(IPEndPoint, byte[])>();
            responses = outgoing;
            if (!TryDecode(packet, out var kind, out var payload) || !PeerInvitation.IsAllowedAddress(remote.Address)) return null;
            var now = clock();
            foreach (var endpoint in validated.Where(item => now - item.Value > 30_000).Select(item => item.Key).ToArray()) validated.Remove(endpoint);
            if (kind == Challenge && payload.Length == 16)
            {
                outgoing.Add((remote, Encode(Response, payload)));
                if (!validated.ContainsKey(remote)) AddChallenge(remote, outgoing);
                return null;
            }
            if (kind == Response && payload.Length == 16)
            {
                if (!challenges.TryGetValue(remote, out var pending) || now - pending.Time > 5000 ||
                    !CryptographicOperations.FixedTimeEquals(payload, pending.Token)) return null;
                challenges.Remove(remote);
                if (validated.Count >= 9 && !validated.ContainsKey(remote)) return null;
                validated[remote] = now;
                if (selected is null || !validated.TryGetValue(selected, out var last) || now - last > 3000)
                { selected = remote; fragmentBytes = 512; sizeToken = null; }
                if (remote.Equals(selected)) ready.TrySetResult();
                return null;
            }
            if (!validated.ContainsKey(remote)) { AddChallenge(remote, outgoing); return null; }
            if (kind == SizeProbe && packet.Length == LargeProbeBytes)
            { outgoing.Add((remote, Encode(SizeResponse, payload))); return null; }
            if (kind == SizeResponse && packet.Length == LargeProbeBytes)
            {
                if (remote.Equals(selected) && sizeToken is not null && now - lastSizeProbe <= 5000 &&
                    CryptographicOperations.FixedTimeEquals(payload.AsSpan(0, 16), sizeToken))
                { fragmentBytes = 1150; lastSizeConfirmed = now; }
                return null;
            }
            return kind is 1 or 2 ? (kind, payload) : null;
        }
    }

    private void AddChallenge(IPEndPoint remote, List<(IPEndPoint, byte[])> outgoing)
    {
        var now = clock();
        foreach (var endpoint in challenges.Where(item => now - item.Value.Time > 5000).Select(item => item.Key).ToArray()) challenges.Remove(endpoint);
        if (challenges.TryGetValue(remote, out var pending))
        {
            if (now - pending.LastSent < 1000) return;
            // Keep the original challenge valid across retries on high-latency paths.
            challenges[remote] = (pending.Time, now, pending.Token);
            outgoing.Add((remote, Encode(Challenge, pending.Token)));
            return;
        }
        if (challenges.Count >= 16 && !challenges.ContainsKey(remote)) return;
        var token = RandomNumberGenerator.GetBytes(16);
        challenges[remote] = (now, now, token);
        outgoing.Add((remote, Encode(Challenge, token)));
    }

    internal byte[] Encode(byte kind, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[HeaderBytes + payload.Length + TagBytes];
        "LFZS"u8.CopyTo(bytes); bytes[4] = kind; bytes[5] = role;
        identifier.CopyTo(bytes, 6);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(22), checked((ulong)Interlocked.Increment(ref sendSequence)));
        payload.CopyTo(bytes.AsSpan(HeaderBytes));
        HMACSHA256.HashData(key, bytes.AsSpan(0, bytes.Length - TagBytes)).AsSpan(0, TagBytes).CopyTo(bytes.AsSpan(bytes.Length - TagBytes));
        return bytes;
    }

    private bool TryDecode(byte[] packet, out byte kind, out byte[] payload)
    {
        kind = 0; payload = [];
        if (packet.Length is < HeaderBytes + TagBytes or > LargeProbeBytes || !packet.AsSpan(0, 4).SequenceEqual("LFZS"u8) ||
            packet[5] != 1 - role || !packet.AsSpan(6, 16).SequenceEqual(identifier)) return false;
        var expected = HMACSHA256.HashData(key, packet.AsSpan(0, packet.Length - TagBytes));
        if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, TagBytes), packet.AsSpan(packet.Length - TagBytes))) return false;
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(22));
        if (sequence == 0) return false;
        if (sequence > highestReceived)
        {
            var advance = sequence - highestReceived;
            receivedMask = advance >= 64 ? 1 : (receivedMask << (int)advance) | 1;
            highestReceived = sequence;
        }
        else
        {
            var age = highestReceived - sequence;
            if (age >= 64 || (receivedMask & (1UL << (int)age)) != 0) return false;
            receivedMask |= 1UL << (int)age;
        }
        kind = packet[4]; payload = packet[HeaderBytes..^TagBytes];
        return true;
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
