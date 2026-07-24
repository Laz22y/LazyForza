using LazyForza.Domain;
using System.IO;
using LazyForza.Modules.Abstractions;
using LazyForza.Storage;
using LazyForza.Telemetry;

namespace LazyForza.App;

internal sealed class TelemetryRecorderController(ITelemetryFeed telemetry, DataDirectoryService directories) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ITelemetrySubscription? subscription;
    private TelemetryRecordingWriter? writer;
    private CancellationTokenSource? cancellation;
    private Task? task;

    public bool IsRecording => task is not null;
    public string? CurrentPath { get; private set; }
    public long FramesWritten { get; private set; }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (task is not null) return;
            directories.EnsureCreated();
            CurrentPath = Path.Combine(directories.RecordingsPath, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.lfztelemetry");
            subscription = await telemetry.SubscribeAsync("raw-recorder", cancellationToken);
            writer = await TelemetryRecordingWriter.CreateAsync(CurrentPath,
                new RecordingMetadata("LazyForza", 1, telemetry.Latest?.Source ?? TelemetrySourceKind.Live, DateTimeOffset.UtcNow, "Raw 324-byte Data Out packets"), cancellationToken);
            cancellation = new CancellationTokenSource();
            FramesWritten = 0;
            task = Task.Run(() => RecordAsync(cancellation.Token), CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (task is null) return;
            cancellation?.Cancel();
            if (subscription is not null) await subscription.DisposeAsync();
            try { await task; } catch (OperationCanceledException) { }
            if (writer is not null) await writer.DisposeAsync();
            cancellation?.Dispose();
            subscription = null;
            writer = null;
            cancellation = null;
            task = null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RecordAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in subscription!.Frames.ReadAllAsync(cancellationToken))
        {
            await writer!.WriteAsync(frame, cancellationToken);
            FramesWritten++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        gate.Dispose();
    }
}

internal sealed class RollingLog : IDisposable
{
    private readonly object gate = new();
    private readonly string path;
    private readonly StreamWriter writer;

    public RollingLog(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "lazyforza.log");
        if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
        {
            var previous = Path.Combine(directory, "lazyforza.previous.log");
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public void Write(string message)
    {
        lock (gate) writer.WriteLine($"{DateTimeOffset.Now:O} [INFO] {message.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}");
    }

    public void Dispose()
    {
        lock (gate) writer.Dispose();
    }
}
