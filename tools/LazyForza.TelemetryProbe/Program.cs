using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LazyForza.Telemetry;

// Development-only UDP source/sink. No product forwarding is installed or silently substituted.
return await Probe.RunAsync(args);

internal static class Probe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "help")
        {
            Console.WriteLine("""
                LazyForza TelemetryProbe (development tool)
                  send    --target 127.0.0.1:2299 [--hz 60 --count 600] [--recording raw.lfztelemetry]
                  receive --listen 127.0.0.1:2300 [--seconds 15]
                  verify  --target 127.0.0.1:2299 --listen 127.0.0.1:2300 [--hz 60 --count 600 --grace 3]
                  self-test [--hz 60 --count 120]
                All modes: --report <new report.json> (existing files are never overwritten).
                verify requires an EXISTING forwarder from target to listen. No hidden forwarding.
                self-test verifies this tool only, via loopback; it does not prove LazyForza forwards packets.
                Synthetic packets preserve the FH6 layout; use isolated application data for synthetic Live input.
                Ctrl+C cancels, prints available statistics and exits with code 2.
                """);
            return 0;
        }
        var report = new Report { Mode = args[0] };
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = Options.Parse(args.Skip(1).ToArray());
            var hz = options.Number("hz", 60, 1, 500);
            var count = options.Number("count", 600, 1, 100_000);
            var seconds = options.Number("seconds", 15, 1, 3600);
            var grace = options.Number("grace", 3, 1, 30);
            var output = options.Get("report");
            if (output is not null && File.Exists(output)) throw new ArgumentException("Report already exists.");
            var clock = Stopwatch.StartNew();
            if (args[0] == "send")
            {
                var target = options.Endpoint("target");
                using var sender = new UdpClient(target.AddressFamily);
                if (options.Get("recording") is { } recording)
                {
                    report.Source = "raw-recording";
                    await new TelemetryReplaySource(recording, speed: 1).RunAsync(async frame =>
                    {
                        await sender.SendAsync(frame.RawPacket, target, cancellation.Token);
                        report.Sent++;
                    }, _ => throw new InvalidDataException("Invalid raw recording."), cancellation.Token);
                }
                else await SendAsync(sender, target, hz, count, report, null, cancellation.Token);
            }
            else if (args[0] is "receive" or "verify" or "self-test")
            {
                if (options.Get("recording") is not null) throw new ArgumentException("--recording is supported by send only.");
                var verify = args[0] != "receive";
                if (!verify) report.Source = "external-udp";
                var self = args[0] == "self-test";
                using var receiver = Bind(self ? new IPEndPoint(IPAddress.Loopback, 0) : options.Endpoint("listen"));
                var listen = (IPEndPoint)receiver.Client.LocalEndPoint!;
                var target = verify ? self ? listen : options.Endpoint("target") : null;
                if (!self && target is not null && target.Port == listen.Port &&
                    (listen.Address.Equals(IPAddress.Any) || listen.Address.Equals(IPAddress.IPv6Any) ||
                     target.Address.Equals(listen.Address) || IPAddress.IsLoopback(target.Address) && IPAddress.IsLoopback(listen.Address)))
                    throw new ArgumentException("Input and output endpoints must differ.");
                var sentAt = new long[count];
                var seen = new bool[count];
                var latency = new List<double>(verify ? count : 0);
                using var receiving = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                var receive = ReceiveAsync(receiver, hz, verify, sentAt, seen, report, latency, receiving.Token);
                try
                {
                    if (target is not null)
                    {
                        using var sender = new UdpClient(target.AddressFamily);
                        await SendAsync(sender, target, hz, count, report, sentAt, cancellation.Token);
                        await Task.Delay(TimeSpan.FromSeconds(grace), cancellation.Token);
                    }
                    else await Task.Delay(TimeSpan.FromSeconds(seconds), cancellation.Token);
                }
                finally
                {
                    await receiving.CancelAsync();
                    await receive;
                    if (verify)
                    {
                        report.Missing = report.Sent - seen.Take(report.Sent).Count(s => s);
                        latency.Sort();
                        report.LatencyMedianMs = Percentile(latency, .5);
                        report.LatencyP95Ms = Percentile(latency, .95);
                        report.Passed = report.Sent > 0 && report.Missing == 0 && report.Modified == 0 && report.Unexpected == 0 && report.Duplicates == 0 && report.OutOfOrder == 0;
                    }
                }
            }
            else throw new ArgumentException("Unknown mode; use --help.");
            report.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            report.ReceivedHz = report.ActiveReceiveSeconds > 0 ? (report.Received - 1) / report.ActiveReceiveSeconds : 0;
            Print(report, output);
            return report.Passed == false ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { report.Cancelled = true; Print(report, null); return 2; }
        catch (Exception e) when (e is ArgumentException or IOException or SocketException or InvalidOperationException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}"); return 2; }
        finally { Console.CancelKeyPress -= handler; }
    }
    private static UdpClient Bind(IPEndPoint endpoint)
    {
        var socket = new UdpClient(endpoint.AddressFamily) { ExclusiveAddressUse = true };
        try { socket.Client.Bind(endpoint); return socket; }
        catch { socket.Dispose(); throw; }
    }
    private static async Task SendAsync(UdpClient sender, IPEndPoint target, int hz, int count, Report report, long[]? sentAt, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var delay = TimeSpan.FromSeconds(i / (double)hz) - clock.Elapsed;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            var packet = Packet(i, hz);
            if (sentAt is not null) Volatile.Write(ref sentAt[i], Stopwatch.GetTimestamp());
            await sender.SendAsync(packet, target, token);
            report.Sent++;
        }
    }
    private static byte[] Packet(int index, int hz)
    {
        var packet = Fh6PacketBuilder.BuildDemoPacket(index, hz);
        // The trailing byte is opaque, not a new field. Exercise byte-preserving forwarding.
        packet[323] = (byte)(index % 251);
        return packet;
    }
    private static async Task ReceiveAsync(UdpClient socket, int hz, bool verify, long[] sentAt, bool[] seen,
        Report report, List<double> latency, CancellationToken token)
    {
        var last = -1; long firstAt = 0;
        var indexByTimestamp = verify ? Enumerable.Range(0, seen.Length).ToDictionary(
            i => BinaryPrimitives.ReadUInt32LittleEndian(Packet(i, hz).AsSpan(4)), i => i) : null;
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(token);
                var now = Stopwatch.GetTimestamp();
                if (firstAt == 0) firstAt = now;
                report.ActiveReceiveSeconds = Stopwatch.GetElapsedTime(firstAt, now).TotalSeconds;
                var bytes = received.Buffer; report.Received++;
                if (bytes.Length != 324) { report.Unexpected++; continue; }
                if (!verify) continue;
                var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
                if (!indexByTimestamp!.TryGetValue(timestamp, out var index) || Volatile.Read(ref sentAt[index]) == 0)
                { report.Unexpected++; continue; }
                if (!bytes.AsSpan().SequenceEqual(Packet(index, hz))) { report.Modified++; continue; }
                if (seen[index]) { report.Duplicates++; continue; }
                if (index < last) report.OutOfOrder++;
                last = Math.Max(last, index); seen[index] = true;
                latency.Add(Stopwatch.GetElapsedTime(Volatile.Read(ref sentAt[index]), now).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private static double? Percentile(List<double> sorted, double value) => sorted.Count == 0 ? null : sorted[(int)Math.Ceiling((sorted.Count - 1) * value)];
    private static void Print(Report report, string? path)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (path is not null)
        { using var file = new FileStream(path, FileMode.CreateNew); using var writer = new StreamWriter(file); writer.Write(json); }
    }
    private sealed class Report
    {
        public string Mode { get; init; } = "";
        public string Source { get; set; } = "synthetic";
        public string Scope => Mode == "self-test" ? "tool-loopback-only" : Mode == "verify" ? "external-forwarding-path" : "udp-endpoint-only";
        public int Sent { get; set; }
        public int Received { get; set; }
        public int Missing { get; set; }
        public int Modified { get; set; }
        public int Duplicates { get; set; }
        public int OutOfOrder { get; set; }
        public int Unexpected { get; set; }
        public double ElapsedSeconds { get; set; }
        public double ActiveReceiveSeconds { get; set; }
        public double ReceivedHz { get; set; }
        public double? LatencyMedianMs { get; set; }
        public double? LatencyP95Ms { get; set; }
        public bool? Passed { get; set; }
        public bool Cancelled { get; set; }
    }
    private sealed class Options(Dictionary<string, string> values)
    {
        internal string? Get(string name) => values.GetValueOrDefault(name);
        internal int Number(string name, int fallback, int min, int max) => Get(name) is not { } value ? fallback :
            int.TryParse(value, out var number) && number >= min && number <= max ? number : throw new ArgumentException($"Invalid --{name}.");
        internal IPEndPoint Endpoint(string name) => Get(name) is { } value && IPEndPoint.TryParse(value, out var endpoint) &&
            endpoint.Port is > 0 and <= 65535 && !endpoint.Address.Equals(IPAddress.Broadcast) &&
            (name != "target" || !endpoint.Address.Equals(IPAddress.Any) && !endpoint.Address.Equals(IPAddress.IPv6Any)) &&
            !endpoint.Address.IsIPv6Multicast && (endpoint.AddressFamily != AddressFamily.InterNetwork || endpoint.Address.GetAddressBytes()[0] < 224)
                ? endpoint : throw new ArgumentException($"Invalid --{name}: use an IP literal and port.");
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || !args[i].StartsWith("--")) throw new ArgumentException("Expected --option value.");
                var key = args[i][2..];
                if (key is not ("target" or "listen" or "hz" or "count" or "seconds" or "grace" or "report" or "recording") || !values.TryAdd(key, args[i + 1]))
                    throw new ArgumentException("Unknown or repeated option.");
            }
            return new(values);
        }
    }
}
