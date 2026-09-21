using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using LazyForza.Update;

namespace LazyForza.EstatePeer;

/// <summary>Stages immutable installs and switches a small local selection only while no host is running.</summary>
public sealed class PeerComponentUpdateManager
{
    private sealed record Selection(PeerSignedComponent? Active, PeerSignedComponent? Previous, bool HasPrevious, long HighestSequence);
    private readonly string root;
    private readonly string rooms;
    private readonly PeerComponentCatalog baseline;
    private readonly PeerComponentDistribution distribution;
    private readonly UpdateSourceKind preferredSource;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Selection selection = new(null, null, false, 0);
    private string SelectionPath => Path.Combine(root, "component-selection.json");
    public PeerComponentStore Current { get; private set; }
    public bool CanRollback => selection.HasPrevious;

    public PeerComponentUpdateManager(string root, string rooms, PeerComponentCatalog baseline,
        PeerComponentDistribution distribution, UpdateSourceKind preferredSource)
    {
        this.root = root; this.rooms = rooms; this.baseline = baseline;
        this.distribution = distribution; this.preferredSource = preferredSource;
        Current = new PeerComponentStore(root, baseline, preferredSource);
    }

    public async Task RestoreAsync(CancellationToken token)
    {
        if (!File.Exists(SelectionPath) || new FileInfo(SelectionPath).Length > 1024 * 1024) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Selection>(await File.ReadAllTextAsync(SelectionPath, token));
            if (saved is null || saved.HighestSequence < 0) return;
            var candidate = saved.Active is null ? null : distribution.Verify(saved.Active, requireFresh: false);
            var catalog = candidate?.Release.Catalog ?? baseline;
            var highest = Math.Max(saved.HighestSequence, candidate?.Release.Sequence ?? 0);
            if (catalog.CompareVersionTo(baseline) < 0)
            {
                selection = new(null, null, false, highest);
                return;
            }
            // Keep the trusted catalog even if the install was removed or damaged, so repair cannot downgrade it.
            // Launch still requires FindVerifiedExecutableAsync to validate every installed file.
            selection = saved with { HighestSequence = highest };
            Current = new PeerComponentStore(root, catalog, preferredSource);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
    }

    public async Task<PeerComponentCandidate?> CheckAsync(CancellationToken token)
    {
        using var client = new HttpClient();
        return await distribution.CheckAsync(preferredSource, selection.HighestSequence, Current.Catalog.Version, client, token, Current.Catalog.Revision);
    }

    public async Task InstallAsync(PeerComponentCandidate candidate, string? archive,
        Func<bool> hostRunning, IProgress<double>? progress, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureStopped(hostRunning);
            // Re-verify at install time: callers cannot replace the authenticated catalog or reuse an expired check.
            candidate = distribution.Verify(candidate.Signed);
            if (candidate.Release.Sequence < selection.HighestSequence ||
                candidate.Release.Catalog.CompareVersionTo(Current.Catalog) <= 0)
                throw new InvalidDataException("组件版本不能通过更新降级。");
            var hadPrevious = await Current.FindVerifiedExecutableAsync(token) is not null;
            var next = new PeerComponentStore(root, candidate.Release.Catalog, preferredSource);
            if (await next.FindVerifiedExecutableAsync(token) is null)
            {
                if (archive is null) await next.DownloadAndInstallAsync(progress, token);
                else await next.InstallAsync(archive, token);
            }
            EnsureStopped(hostRunning);
            await BackupRoomsAsync(token);
            EnsureStopped(hostRunning);
            await SelectAsync(new(candidate.Signed, selection.Active, hadPrevious,
                Math.Max(selection.HighestSequence, candidate.Release.Sequence)), next, token);
        }
        finally { gate.Release(); }
    }

    public async Task ImportAsync(string archive, Func<bool> hostRunning, CancellationToken token)
    {
        EnsureStopped(hostRunning);
        await using (var input = File.OpenRead(archive))
        {
            if (input.Length == Current.Catalog.DownloadBytes && Convert.ToHexString(await SHA256.HashDataAsync(input, token))
                .Equals(Current.Catalog.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                await InstallCurrentAsync(archive, hostRunning, null, token);
                return;
            }
        }
        var signedPath = Path.Combine(Path.GetDirectoryName(archive)!, PeerComponentDistribution.ManifestName);
        if (File.Exists(signedPath))
        {
            if (new FileInfo(signedPath).Length > PeerComponentDistribution.MaximumManifestBytes)
                throw new InvalidDataException("组件更新清单大小无效。");
            var signed = JsonSerializer.Deserialize<PeerSignedComponent>(await File.ReadAllTextAsync(signedPath, token))
                ?? throw new InvalidDataException("组件更新清单为空。");
            var candidate = distribution.Verify(signed);
            if (candidate.Release.Catalog.ArchiveSha256 != Current.Catalog.ArchiveSha256)
            {
                await InstallAsync(candidate, archive, hostRunning, null, token);
                return;
            }
        }
        throw new InvalidDataException("离线更新需要将 ZIP 与签名清单放在同一文件夹。");
    }

    public async Task InstallCurrentAsync(string? archive, Func<bool> hostRunning, IProgress<double>? progress, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureStopped(hostRunning);
            if (await Current.FindVerifiedExecutableAsync(token) is not null) return;
            if (Directory.Exists(Current.InstallDirectory)) Current.Uninstall();
            if (archive is null) await Current.DownloadAndInstallAsync(progress, token);
            else await Current.InstallAsync(archive, token);
        }
        finally { gate.Release(); }
    }

    public async Task RollbackAsync(Func<bool> hostRunning, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureStopped(hostRunning);
            if (!selection.HasPrevious) throw new InvalidOperationException("没有可恢复的上一组件版本。");
            var previous = selection.Previous is null ? baseline : distribution.Verify(selection.Previous, requireFresh: false).Release.Catalog;
            var store = new PeerComponentStore(root, previous, preferredSource);
            if (await store.FindVerifiedExecutableAsync(token) is null) throw new IOException("上一组件版本不完整，无法恢复。");
            await BackupRoomsAsync(token);
            EnsureStopped(hostRunning);
            // Compatible project format 1 is required for all accepted updates. Room data is never overwritten on rollback.
            await SelectAsync(selection with { Active = selection.Previous, Previous = null, HasPrevious = false }, store, token);
        }
        finally { gate.Release(); }
    }

    private async Task SelectAsync(Selection next, PeerComponentStore store, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        var temporary = SelectionPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, SelectionPath, overwrite: true);
            selection = next; Current = store;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task BackupRoomsAsync(CancellationToken token)
    {
        if (!Directory.Exists(rooms)) return;
        var directory = Path.Combine(Path.GetDirectoryName(root)!, "Backups", "Components");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"rooms-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        try
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var pending = new Stack<string>(); pending.Push(rooms);
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件升级备份不支持链接目录。");
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("组件升级备份不支持链接文件。");
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                    await using var input = File.OpenRead(entry);
                    await using var output = zip.CreateEntry(Path.GetRelativePath(rooms, entry).Replace('\\', '/'), CompressionLevel.Fastest).Open();
                    await input.CopyToAsync(output, token);
                }
            }
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    private static void EnsureStopped(Func<bool> hostRunning)
    {
        if (hostRunning()) throw new InvalidOperationException("请先关闭直连房间，再更新房主组件。");
    }
}
