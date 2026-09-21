using System.IO;
using System.Text.Json;
using System.Windows;
using LazyForza.EstatePeer;
using LazyForza.Modules.EstateRace;
using LazyForza.Storage;

namespace LazyForza.App;

internal sealed record PeerTrackChoice(Guid Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed record PeerProjectInfo(Guid Id, Guid TrackId, string Name, string TrackName, string Revision,
    string TrackHash, int SectorCount, int RaceLaps, DateTimeOffset CreatedAt)
{
    public override string ToString() => $"{Name} · {CreatedAt.LocalDateTime:MM-dd HH:mm:ss}";
}

internal sealed record PeerRoomDraft(string Name, string Password, Guid TrackId, Guid? ProjectId,
    int Port, string? ExternalAddress, int? ExternalPort, int RaceLaps);

internal sealed partial class MainWindow
{
    private PeerHostProcess? estatePeerHost;
    private EstatePeerWindow? estatePeerWindow;
    private bool openingEstatePeerWindow;
    private string EstatePeerRoot => Path.Combine(directories.Root, "EstatePeer");

    private async Task OpenEstatePeerWindowAsync()
    {
        if (estatePeerWindow is { } existingWindow)
        {
            if (existingWindow.WindowState == WindowState.Minimized) existingWindow.WindowState = WindowState.Normal;
            existingWindow.Activate();
            return;
        }
        if (openingEstatePeerWindow) return;
        openingEstatePeerWindow = true;
        try
        {
            var module = moduleManager.Modules.OfType<EstateRaceModule>().Single();
            var saved = await module.LoadSavedProfileAsync(lifetimeCancellation.Token);
            var tracks = store.ListTracks().Where(track => store.LoadEstateTrackDefinition(track.Id) is not null)
                .Select(track => new PeerTrackChoice(track.Id, track.Name)).ToArray();
            var projects = new List<PeerProjectInfo>();
            var roomRoot = Path.Combine(EstatePeerRoot, "Rooms");
            if (Directory.Exists(roomRoot))
            {
                foreach (var folder in Directory.EnumerateDirectories(roomRoot).Take(100))
                {
                    var path = Path.Combine(folder, "project.json");
                    if (!File.Exists(path) || new FileInfo(path).Length > 16384) continue;
                    try
                    {
                        var project = JsonSerializer.Deserialize<PeerProjectInfo>(File.ReadAllText(path));
                        if (project is not null && project.Id.ToString("N").Equals(Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)) projects.Add(project);
                    }
                    catch (JsonException) { }
                }
            }
            PeerComponentStore? component = null;
            PeerComponentUpdateManager? componentUpdates = null;
            using (var catalog = typeof(MainWindow).Assembly.GetManifestResourceStream("LazyForza.EstatePeer.ComponentCatalog"))
                if (catalog is not null && PeerComponentStore.ReadCatalog(catalog) is { } manifest)
                    component = new PeerComponentStore(Path.Combine(EstatePeerRoot, "Components"), manifest);
            using (var key = typeof(MainWindow).Assembly.GetManifestResourceStream("LazyForza.EstatePeer.ComponentPublicKey"))
            {
                if (component is not null && key is not null)
                {
                    using var reader = new StreamReader(key);
                    var distribution = new PeerComponentDistribution(await reader.ReadToEndAsync(lifetimeCancellation.Token),
                        ApplicationVersionInfo.Informational, updateManager.IsUpdateMandatory);
                    componentUpdates = new PeerComponentUpdateManager(Path.Combine(EstatePeerRoot, "Components"), roomRoot,
                        component.Catalog, distribution, updateManager.PreferredSource);
                    await componentUpdates.RestoreAsync(lifetimeCancellation.Token);
                    component = componentUpdates.Current;
                }
            }
            var installed = component is not null && await component.FindVerifiedExecutableAsync(lifetimeCancellation.Token) is not null;
            projects.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
            var recentPath = Path.Combine(EstatePeerRoot, "last-project.txt");
            Guid? recentProject = File.Exists(recentPath) && Guid.TryParse(File.ReadAllText(recentPath), out var recent) ? recent : null;
            var dialog = new EstatePeerWindow(saved, tracks, projects, component,
                () => estatePeerHost,
                async (draft, token) =>
                {
                    var activeComponent = componentUpdates?.Current ?? component;
                    if (activeComponent is null) throw new InvalidOperationException("此客户端尚未配置房主组件，请等待匹配的组件版本。");
                    var executable = await activeComponent.FindVerifiedExecutableAsync(token)
                        ?? throw new InvalidOperationException("请先安装或修复房主组件。");
                    if (estatePeerHost is { IsRunning: true }) throw new InvalidOperationException("请先关闭当前直连房间。");
                    if (estatePeerHost is not null) await StopEstatePeerAsync();
                    using var creation = draft.ProjectId is null ? new PeerProjectCreation(roomRoot) : null;
                    var id = draft.ProjectId ?? creation!.Id;
                    var folder = creation?.Folder ?? Path.Combine(roomRoot, id.ToString("N"));
                    var packagePath = Path.Combine(folder, "track.lfzestate");
                    PeerProjectInfo project;
                    if (draft.ProjectId is not null)
                    {
                        project = projects.Single(item => item.Id == id);
                    }
                    else
                    {
                        var packages = new EstateTrackPackageService(store, CurrentApplicationVersion());
                        packages.Export(draft.TrackId, packagePath, token);
                        var identity = packages.Identify(draft.TrackId);
                        project = new(id, draft.TrackId, draft.Name.Trim(), identity.TrackName, identity.MapRevision,
                            identity.TrackFingerprintSha256, Math.Max(1, identity.SectorCount), draft.RaceLaps, DateTimeOffset.UtcNow);
                    }
                    var start = new PeerHostStart(folder, project.Name, draft.Password, draft.Port, draft.ExternalAddress, draft.ExternalPort,
                        project.TrackId.ToString("D"), project.TrackName, project.Revision, project.TrackHash, packagePath,
                        project.SectorCount, project.RaceLaps, draft.ProjectId is not null);
                    // A project becomes visible only after the helper has opened its listeners.
                    // A failed attempt can be retried without adding an empty item to the project list.
                    var started = await PeerHostProcess.StartAsync(executable, start, token);
                    try
                    {
                        if (draft.ProjectId is null)
                        {
                            creation!.Commit(JsonSerializer.Serialize(project));
                            projects.Insert(0, project);
                        }
                        estatePeerHost = started;
                        try { File.WriteAllText(recentPath, id.ToString("N")); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    catch { await started.DisposeAsync(); throw; }
                    return estatePeerHost;
                },
                JoinEstateRaceProfileAsync,
                StopEstatePeerAsync,
                () => module.State.IsConnected,
                () => module.State.Session is { } session
                    ? AppLocalization.Format("peer.members", "已加入 {0} 位车手 · {1}", session.Participants.Count(item => item.IsConnected), RacePhaseLabel(session.Phase))
                    : AppLocalization.Literal("监听已就绪；其他设备连入后才代表对应路径可达。"), installed, recentProject, componentUpdates);
            estatePeerWindow = dialog;
            dialog.Closed += (_, _) => estatePeerWindow = null;
            dialog.Show();
        }
        catch (Exception error)
        {
            AppDialog.Show(this, AppLocalization.Literal(error.Message), AppLocalization.Literal("直连房间"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { openingEstatePeerWindow = false; }
    }

    internal async Task StopEstatePeerAsync()
    {
        if (estatePeerHost is not { } host) return;
        estatePeerHost = null;
        try
        {
            var module = moduleManager.Modules.OfType<EstateRaceModule>().Single();
            if (module.ActiveProfile?.Peer?.Invitation.RoomId == host.Invitation.RoomId) await module.DisconnectAsync();
        }
        finally { await host.DisposeAsync(); }
    }
}
