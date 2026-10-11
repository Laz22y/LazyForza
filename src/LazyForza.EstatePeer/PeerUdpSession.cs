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
    private IReadOnlyList<PeerEndpoint> candidates;
    private IReadOnlyList<PeerEndpoint> localCandidates = [];
    private readonly byte[] updateKey;
    private readonly Dictionary<IPEndPoint, (double Rtt, int Samples)> quality = [];
    private long switchedAt, lastBackup = long.MinValue / 2, lastHello = long.MinValue / 2, lastUpdate = long.MinValue / 2;
    private long exploringUntil, lastProbe = long.MinValue / 2;
    private int candidateCursor;
    private bool remoteUpdates, connected;
    internal void SetLocalCandidates(IReadOnlyList<PeerEndpoint> value)
    { lock (sync) localCandidates = value.Where(e => PeerInvitation.IsAllowedAddress(e.Address)).Take(8).ToArray(); }
    internal void UpdateRemoteCandidates(IReadOnlyList<PeerEndpoint> value)
    { lock (sync) { candidates = value; exploringUntil = clock() + 20_000; lastProbe = long.MinValue / 2; } }
    internal void MarkConnected() { lock (sync) connected = true; }
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
    private ulong highestReceived, receivedMask, lastCandidateUpdate;
    internal string Identifier => Convert.ToHexString(identifier);
    internal IPEndPoint? Selected { get { lock (sync) return selected; } }
    internal int FragmentBytes { get { lock (sync) return fragmentBytes; } }

    internal PeerUdpSession(PeerReceipt receipt, IReadOnlyList<PeerEndpoint> candidates, bool host, Func<long>? clock = null)
    {
        key = PeerPathAuthentication.Key(receipt, "udp-v2");
        updateKey = PeerPathAuthentication.Key(receipt, "candidate-v1");
        identifier = PeerPathAuthentication.Identifier(receipt);
        this.candidates = candidates;
        role = host ? (byte)1 : (byte)0;
        this.clock = clock ?? (() => Environment.TickCount64);
        exploringUntil = this.clock() + 20_000;
    }

    internal Task BeginAttempt()
    {
        lock (sync)
        {
            ready = NewCompletion();
            challenges.Clear();
            exploringUntil = clock() + 20_000; lastProbe = long.MinValue / 2;
            // A previous small/large-packet success is not evidence for this attempt.
            fragmentBytes = 512; sizeToken = null; lastSizeProbe = 0; lastSizeConfirmed = long.MinValue / 2;
            return ready.Task;
        }
    }

    internal IReadOnlyList<(IPEndPoint Remote, byte[] Bytes)> Probe(int budget = 10)
    {
        lock (sync)
        {
            var outgoing = new List<(IPEndPoint, byte[])>();
            var now = clock();
            var selectedFresh = selected is not null && validated.TryGetValue(selected, out var seen) && now - seen <= 3000;
            var interval = connected || now < exploringUntil ? 1000 : 5000;
            if (now - lastProbe < interval || budget < 1) return outgoing;
            lastProbe = now;
            if (selected is not null) AddChallenge(selected, outgoing);
            var endpoints = validated.OrderByDescending(item => item.Value).Select(item => item.Key)
                .Concat(candidates.Select(item => new IPEndPoint(item.Address, item.Port)))
                .Where(e => !e.Equals(selected)).Distinct().Take(8).ToArray();
            if (!connected || !selectedFresh || now - lastBackup >= 5000)
            {
                var remaining = Math.Max(0, budget - outgoing.Count);
                for (var i = 0; i < Math.Min(remaining, endpoints.Length); i++)
                    AddChallenge(endpoints[(candidateCursor + i) % endpoints.Length], outgoing);
                candidateCursor = endpoints.Length == 0 ? 0 : (candidateCursor + remaining) % endpoints.Length;
                lastBackup = now;
            }
            if (sizeToken is not null && lastSizeConfirmed < lastSizeProbe && now - lastSizeProbe > 3000)
                fragmentBytes = 512;
            if (selected is not null && outgoing.Count < budget && (sizeToken is null || now - lastSizeProbe >= 10_000))
            {
                sizeToken = RandomNumberGenerator.GetBytes(16); lastSizeProbe = now;
                var payload = new byte[LargeProbeBytes - HeaderBytes - TagBytes];
                sizeToken.CopyTo(payload, 0);
                outgoing.Add((selected, Encode(SizeProbe, payload)));
            }
            // Unknown kinds are ignored by LFZR2 peers. A validated capability exchange precedes updates.
            if (connected && selectedFresh && selected is not null && outgoing.Count < budget && now - lastHello >= 10_000)
            { lastHello = now; outgoing.Add((selected, Encode(7, [1]))); }
            if (connected && remoteUpdates && selectedFresh && selected is not null && localCandidates.Count > 0 &&
                outgoing.Count < budget && now - lastUpdate >= 10_000)
            {
                lastUpdate = now;
                using var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) PeerCode.WriteEndpoints(writer, localCandidates);
                var plain = stream.ToArray(); var payload = new byte[12 + plain.Length + 16];
                RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
                using var aes = new AesGcm(updateKey, 16);
                aes.Encrypt(payload.AsSpan(0, 12), plain, payload.AsSpan(12, plain.Length), payload.AsSpan(12 + plain.Length), identifier);
                outgoing.Add((selected, Encode(8, payload)));
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
            foreach (var endpoint in validated.Where(item => now - item.Value > 30_000).Select(item => item.Key).ToArray()) { validated.Remove(endpoint); quality.Remove(endpoint); }
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
                if (pending.Time == pending.LastSent)
                {
                    var rtt = Math.Max(0, now - pending.Time);
                    quality[remote] = quality.TryGetValue(remote, out var q)
                        ? (q.Rtt * .75 + rtt * .25, Math.Min(100, q.Samples + 1)) : (rtt, 1);
                }
                var better = selected is not null && quality.TryGetValue(remote, out var candidate) && candidate.Samples >= 3 &&
                    quality.TryGetValue(selected, out var current) && current.Samples >= 3 && now - switchedAt >= 15_000 &&
                    candidate.Rtt + 10 < current.Rtt * .75;
                if (selected is null || !validated.TryGetValue(selected, out var last) || now - last > 3000 || better)
                {
                    selected = remote; switchedAt = now; fragmentBytes = 512; sizeToken = null;
                    PeerDiagnostics.Record(PeerStage.Path, PeerOutcome.Changed, address: remote.Address,
                        milliseconds: quality.TryGetValue(remote, out var selectedQuality) ? selectedQuality.Rtt : null);
                }
                if (remote.Equals(selected)) ready.TrySetResult();
                return null;
            }
            if (!validated.ContainsKey(remote)) { AddChallenge(remote, outgoing); return null; }
            if (kind == 7 && payload.Length == 1 && payload[0] == 1)
            {
                remoteUpdates = true;
                return null;
            }
            if (kind == 8 && connected && remoteUpdates && payload.Length is >= 29 and <= 256 &&
                BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(22)) > lastCandidateUpdate)
            {
                try
                {
                    var plain = new byte[payload.Length - 28];
                    using var aes = new AesGcm(updateKey, 16);
                    aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(12, plain.Length), payload.AsSpan(12 + plain.Length), plain, identifier);
                    using var reader = new BinaryReader(new MemoryStream(plain));
                    var updated = PeerCode.ReadEndpoints(reader);
                    if (reader.BaseStream.Position == reader.BaseStream.Length && updated.All(e => PeerInvitation.IsAllowedAddress(e.Address)))
                    {
                        lastCandidateUpdate = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(22));
                        if (!candidates.SequenceEqual(updated))
                        { candidates = updated; exploringUntil = now + 20_000; lastBackup = long.MinValue / 2; }
                    }
                }
                catch (Exception error) when (error is CryptographicException or IOException or ArgumentException) { }
                return null;
            }
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
