using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LazyForza.EstatePeer;
using LazyForza.Modules.EstateRace;

namespace LazyForza.App;

internal sealed class EstatePeerWindow : Window
{
    private readonly EstateRaceConnectionProfile saved;
    private readonly IReadOnlyList<PeerTrackChoice> tracks;
    private readonly IReadOnlyList<PeerProjectInfo> projects;
    private PeerComponentStore? component;
    private readonly PeerComponentUpdateManager? componentUpdates;
    private PeerComponentCandidate? componentCandidate;
    private readonly Func<PeerHostProcess?> host;
    private readonly Func<PeerRoomDraft, CancellationToken, Task<PeerHostProcess>> create;
    private readonly Func<EstateRaceConnectionProfile, CancellationToken, Task> join;
    private readonly Func<Task> stop;
    private readonly Func<bool> connected;
    private readonly Func<string> roomStatus;
    private readonly CancellationTokenSource cancellation = new();
    private readonly StackPanel body = new();
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) };
    private readonly DispatcherTimer timer;
    private TextBlock? liveStatus;
    private bool busy;
    private bool creating;
    private bool componentInstalled;
    private string hostPassword = string.Empty;
    private Guid? selectedProjectId;
    private bool newProject;
    private PeerQuicJoin? pendingJoin;
    private readonly Action<string>? clipboardWriter;

    public EstatePeerWindow(EstateRaceConnectionProfile saved, IReadOnlyList<PeerTrackChoice> tracks,
        IReadOnlyList<PeerProjectInfo> projects, PeerComponentStore? component, Func<PeerHostProcess?> host,
        Func<PeerRoomDraft, CancellationToken, Task<PeerHostProcess>> create,
        Func<EstateRaceConnectionProfile, CancellationToken, Task> join, Func<Task> stop, Func<bool> connected, Func<string> roomStatus,
        bool componentInstalled = false, Guid? recentProjectId = null, PeerComponentUpdateManager? componentUpdates = null,
        Action<string>? clipboardWriter = null)
    {
        this.saved = saved; this.tracks = tracks; this.projects = projects; this.component = component;
        this.host = host; this.create = create; this.join = join; this.stop = stop; this.connected = connected; this.roomStatus = roomStatus;
        this.componentInstalled = componentInstalled;
        this.componentUpdates = componentUpdates;
        this.clipboardWriter = clipboardWriter;
        selectedProjectId = projects.FirstOrDefault(item => item.Id == recentProjectId)?.Id ?? projects.FirstOrDefault()?.Id;
        newProject = projects.Count == 0;
        Title = AppLocalization.Literal("直连房间 · 实验");
        Width = 680; Height = Math.Min(760, SystemParameters.WorkArea.Height * .92);
        MinWidth = 520; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true; Topmost = false;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new StackPanel { Margin = new Thickness(28) };
        root.Children.Add(Text("直连房间", 26, "TextBrush"));
        root.Children.Add(Text("房主承载比赛，玩家直接连接。无需房间目录或中继服务。", 12));
        root.Children.Add(body);
        message.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(message);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Closing += (_, args) => { if (busy) args.Cancel = true; };
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (liveStatus is not null) liveStatus.Text = host() is { IsRunning: true } ? roomStatus() : AppLocalization.Literal("房主组件已退出。重新创建时可选择原项目恢复比赛。");
        }, Dispatcher);
        Closed += async (_, _) =>
        {
            timer.Stop(); cancellation.Cancel();
            try { if (pendingJoin is not null) await pendingJoin.DisposeAsync(); }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { }
            finally { cancellation.Dispose(); }
        };
        Render();
    }

    private void Render()
    {
        body.Children.Clear(); liveStatus = null;
        if (host() is { IsRunning: true } running) { RenderHosting(running); return; }
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 8) };
        tabs.Children.Add(ActionButton("加入房间", () => { creating = false; Render(); }, !creating));
        tabs.Children.Add(ActionButton("创建房间", () => { creating = true; Render(); }, creating));
        body.Children.Add(tabs);
        if (creating) RenderCreate(); else RenderJoin();
        AppLocalization.ApplyTo(body);
    }

    private void RenderJoin()
    {
        var code = Input(string.Empty); code.MinHeight = 90; code.TextWrapping = TextWrapping.Wrap; code.MaxLength = PeerInvitation.MaximumCodeLength;
        var password = Password();
        var name = Input(saved.DisplayName); name.MaxLength = 20;
        var observer = new CheckBox { Content = AppLocalization.Literal("以 OB 身份加入"), IsChecked = saved.IsObserver, Margin = new Thickness(0, 10, 0, 4) };
        Field(body, "邀请代码", code);
        body.Children.Add(Text("请粘贴完整代码；短房间编号仅用于核对。", 11));
        Field(body, "房间密码", password);
        Field(body, "显示名称", name);
        body.Children.Add(observer);
        var receiptPanel = new StackPanel();
        body.Children.Add(AsyncButton("加入房间", async () =>
        {
            if (connected()) throw new InvalidOperationException("请先退出当前赛事房间。");
            ValidateIdentity(name.Text, password.Password);
            receiptPanel.Children.Clear();
            if (pendingJoin is not null) { await pendingJoin.DisposeAsync(); pendingJoin = null; }
            message.Text = AppLocalization.Literal("正在寻找可达地址并验证房主…");
            var invitation = PeerInvitation.Parse(code.Text);
            PeerConnection connection;
            try { connection = await PeerConnection.FindAsync(invitation, cancellation.Token); }
            catch (IOException) when (invitation.SupportsUdp && PeerQuicHost.IsSupported)
            {
                pendingJoin = new PeerQuicJoin(invitation);
                var pending = pendingJoin;
                var receipt = Input(pending.Receipt.Encode()); receipt.IsReadOnly = true; receipt.TextWrapping = TextWrapping.Wrap;
                receiptPanel.Children.Clear();
                receiptPanel.Children.Add(Text("将回执发给房主。房主添加后，点击继续连接。回执十分钟内有效。", 12));
                Field(receiptPanel, "连接回执", receipt);
                receiptPanel.Children.Add(CopyButton("复制连接回执", receipt));
                receiptPanel.Children.Add(AsyncButton("继续连接", async () =>
                {
                    message.Text = AppLocalization.Literal("正在等待房主 UDP 响应并建立加密连接…");
                    var verified = await pending.ConnectAsync(cancellation.Token);
                    await join(Profile(verified, password.Password, name.Text, observer.IsChecked == true), cancellation.Token);
                    pendingJoin = null; // The connected module now owns the tunnel.
                    CloseAfterOperation();
                }, primary: true));
                message.Text = AppLocalization.Literal("TCP 地址不可达，已准备 UDP 连接回执。");
                return;
            }
            message.Text = AppLocalization.Literal("房主身份已验证，正在同步赛道并加入…");
            await join(Profile(connection, password.Password, name.Text, observer.IsChecked == true), cancellation.Token);
            CloseAfterOperation();
        }, primary: true));
        body.Children.Add(receiptPanel);
        body.Children.Add(NetworkHelp());
    }

    private void RenderCreate()
    {
        if (component is null)
        {
            body.Children.Add(Text("此客户端尚未配置房主组件，请等待匹配的组件版本。", 13, "WarningBrush"));
            body.Children.Add(Text("加入房间不需要下载房主组件。", 12));
            return;
        }
        var install = new StackPanel();
        install.Children.Add(Text(componentInstalled ? "房主组件 · 已安装" : "按需安装房主组件", 16, "TextBrush"));
        install.Children.Add(Text(AppLocalization.Format("peer.componentSize", "下载 {0:0.0} MiB · 安装后 {1:0.0} MiB · {2}",
            component.Catalog.DownloadBytes / 1048576d, component.InstalledBytes / 1048576d, component.Catalog.ReleaseId), 12));
        var installActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var download = AsyncButton("下载组件", async () =>
        {
            var progress = new Progress<double>(value => message.Text = AppLocalization.Format("peer.downloadProgress", "正在下载房主组件 · {0:P0}", value));
            if (componentUpdates is null) await component.DownloadAndInstallAsync(progress, cancellation.Token);
            else await componentUpdates.InstallCurrentAsync(null, () => host() is { IsRunning: true }, progress, cancellation.Token);
            componentInstalled = true; Render();
            message.Text = AppLocalization.Literal("房主组件已安装，可以创建房间。");
        });
        download.IsEnabled = component.Catalog.DownloadUrl is not null || component.Catalog.DownloadUrls is { Count: > 0 };
        installActions.Children.Add(download);
        installActions.Children.Add(AsyncButton("导入离线包", async () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "LazyForza component (*.zip)|*.zip", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            if (componentUpdates is null) await component.InstallAsync(picker.FileName, cancellation.Token);
            else
            {
                await componentUpdates.ImportAsync(picker.FileName, () => host() is { IsRunning: true }, cancellation.Token);
                component = componentUpdates.Current;
            }
            componentInstalled = true; Render();
            message.Text = AppLocalization.Literal("房主组件已安装，可以创建房间。");
        }));
        if (componentInstalled)
        {
            install.Children.RemoveAt(0);
            installActions.Children.Remove(download);
            installActions.Children.Add(AsyncButton("卸载组件", () => { component.Uninstall(); componentInstalled = false; Render(); message.Text = AppLocalization.Literal("组件已卸载，房间项目仍然保留。"); return Task.CompletedTask; }));
            install.Children.Add(installActions);
        }
        else
        {
            install.Children.Add(installActions);
            install.Children.Add(Text(component.Catalog.DownloadUrl is null && component.Catalog.DownloadUrls is not { Count: > 0 }
                ? "此实验组件尚未发布下载，可导入匹配的离线包。" : "安装后即可离线建房。加入他人房间不需要此组件。", 11));
        }
        if (componentUpdates is not null)
        {
            installActions.Children.Insert(0, AsyncButton("检查组件更新", async () =>
            {
                componentCandidate = await componentUpdates.CheckAsync(cancellation.Token);
                Render();
                message.Text = AppLocalization.Literal(componentCandidate is null
                    ? "没有适用于当前客户端的房主组件更新。" : "发现兼容的房主组件更新，关闭房间后即可安装。");
            }));
            if (componentCandidate is { } candidate)
            {
                install.Children.Add(Text(AppLocalization.Format("peer.componentUpdate", "可更新至 {0} · 下载 {1:0.0} MiB",
                    candidate.Release.Catalog.ReleaseId, candidate.Release.Catalog.DownloadBytes / 1048576d), 12, "AccentBrush"));
                install.Children.Add(AsyncButton("更新房主组件", async () =>
                {
                    await componentUpdates.InstallAsync(candidate, null, () => host() is { IsRunning: true },
                        new Progress<double>(value => message.Text = AppLocalization.Format("peer.downloadProgress", "正在下载房主组件 · {0:P0}", value)), cancellation.Token);
                    component = componentUpdates.Current;
                    componentInstalled = true; componentCandidate = null; Render();
                    message.Text = AppLocalization.Literal("房主组件已更新，下次建房使用新版本。项目备份已保留。");
                }, primary: true));
            }
            if (componentUpdates.CanRollback)
                installActions.Children.Add(AsyncButton("恢复上一组件版本", async () =>
                {
                    await componentUpdates.RollbackAsync(() => host() is { IsRunning: true }, cancellation.Token);
                    component = componentUpdates.Current; componentInstalled = true; componentCandidate = null; Render();
                    message.Text = AppLocalization.Literal("已恢复上一组件版本，房间项目保持不变。");
                }));
        }
        if (componentInstalled)
        {
            var disclosure = Disclosure("房主组件 · 已安装", install);
            disclosure.IsExpanded = componentCandidate is not null;
            body.Children.Add(disclosure);
        }
        else body.Children.Add(Surface(install));
        if (!componentInstalled) return;

        var projectActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (projects.Count > 0)
            projectActions.Children.Add(ActionButton("打开项目", () => { newProject = false; Render(); }, !newProject));
        projectActions.Children.Add(ActionButton("新建项目", () => { newProject = true; Render(); }, newProject));
        body.Children.Add(projectActions);
        var project = new ComboBox { MinHeight = 40, ItemsSource = projects,
            SelectedItem = newProject ? null : projects.FirstOrDefault(item => item.Id == selectedProjectId) ?? projects.FirstOrDefault() };
        if (!newProject) Field(body, "房间项目", project);
        body.Children.Add(Text(newProject ? "创建独立的比赛项目，赛道与赛果分别保存。" : "继续原有比赛，保留车手、阶段和赛果。", 11));
        var roomName = Input("地产赛事"); roomName.MaxLength = 64;
        var track = new ComboBox { ItemsSource = tracks, SelectedIndex = tracks.Count > 0 ? 0 : -1, MinHeight = 40 };
        var password = Password();
        var projectSummary = Text(string.Empty, 12);
        if (newProject)
        {
            Field(body, "房间名称", roomName);
            Field(body, "地产赛道", track);
        }
        else body.Children.Add(projectSummary);
        Field(body, "房间密码", password);
        var laps = Input("10");
        var port = Input("24878");
        var external = Input(string.Empty);
        var externalPort = Input("24878");
        var advanced = new StackPanel();
        Field(advanced, "正赛圈数", laps);
        Field(advanced, "监听端口（TCP／UDP）", port);
        Field(advanced, "外部或指定网卡 IP（可选）", external);
        Field(advanced, "外部端口", externalPort);
        advanced.Children.Add(Text("自动收集局域网与 IPv6 地址。使用公网 IPv4 端口映射时，TCP 与 UDP 应映射到相同端口。", 11));
        body.Children.Add(Disclosure("比赛与网络设置", advanced));
        project.SelectionChanged += (_, _) =>
        {
            var existing = project.SelectedItem as PeerProjectInfo;
            selectedProjectId = existing?.Id;
            roomName.IsEnabled = track.IsEnabled = laps.IsEnabled = existing is null;
            if (existing is null) return;
            projectSummary.Text = existing.TrackName;
            roomName.Text = existing.Name; laps.Text = existing.RaceLaps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            track.SelectedItem = tracks.FirstOrDefault(item => item.Id == existing.TrackId);
        };
        if (project.SelectedItem is PeerProjectInfo chosen)
        {
            projectSummary.Text = chosen.TrackName;
            roomName.IsEnabled = track.IsEnabled = laps.IsEnabled = false;
            roomName.Text = chosen.Name; laps.Text = chosen.RaceLaps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            track.SelectedItem = tracks.FirstOrDefault(item => item.Id == chosen.TrackId);
        }
        body.Children.Add(AsyncButton(newProject ? "创建并加入" : "打开并加入", async () =>
        {
            if (connected()) throw new InvalidOperationException("请先退出当前赛事房间。");
            ValidateIdentity(saved.DisplayName, password.Password);
            if (!int.TryParse(port.Text, out var localPort) || localPort is < 1024 or > 65535 ||
                !int.TryParse(externalPort.Text, out var publicPort) || publicPort is < 1 or > 65535 ||
                !int.TryParse(laps.Text, out var raceLaps) || raceLaps is < 1 or > 999)
                throw new InvalidOperationException("请填写有效圈数与端口；监听端口范围为 1024–65535。");
            var existing = project.SelectedItem as PeerProjectInfo;
            var selected = track.SelectedItem as PeerTrackChoice;
            if (existing is null && selected is null) throw new InvalidOperationException("请先录入或导入一条地产赛道。");
            if (string.IsNullOrWhiteSpace(roomName.Text)) throw new InvalidOperationException("请填写房间名称。");
            _ = PeerAddresses.Collect(localPort, string.IsNullOrWhiteSpace(external.Text) ? null : external.Text.Trim(), publicPort);
            message.Text = AppLocalization.Literal("正在校验组件并创建房间…");
            var running = await create(new(roomName.Text, password.Password, existing?.TrackId ?? selected!.Id, existing?.Id,
                localPort, string.IsNullOrWhiteSpace(external.Text) ? null : external.Text.Trim(), publicPort, raceLaps), cancellation.Token);
            hostPassword = password.Password;
            selectedProjectId = existing?.Id ?? projects.FirstOrDefault()?.Id; newProject = false;
            Render();
            await JoinHostAsync(running, hostPassword);
            message.Text = AppLocalization.Literal("房间已创建。请将完整邀请代码和密码分享给其他玩家。");
        }, primary: true));
        body.Children.Add(NetworkHelp());
    }

    private void RenderHosting(PeerHostProcess running)
    {
        var summary = new StackPanel();
        summary.Children.Add(Text(AppLocalization.Format("peer.roomNumber", "房间 {0}", running.Invitation.RoomLabel), 22, "TextBrush"));
        liveStatus = Text(roomStatus(), 12);
        summary.Children.Add(liveStatus);
        summary.Children.Add(Text(AppLocalization.Format("peer.hostPort", "本机 TCP 端口 {0} · 邀请有效至 {1:g}", running.Port, running.Invitation.ExpiresAt.LocalDateTime), 11));
        body.Children.Add(Surface(summary));
        var access = new StackPanel();
        var controlPassword = Input(string.Empty); controlPassword.IsReadOnly = true;
        controlPassword.TextWrapping = TextWrapping.Wrap;
        Field(access, "本次总控密码", controlPassword);
        access.Children.Add(Text("仅用于房主本机总控，与房间密码不同；重新打开房间后会更换。", 11));
        var accessActions = new WrapPanel();
        accessActions.Children.Add(AsyncButton("查看总控密码", async () =>
        {
            var reply = await running.CommandAsync(new("controlAccess"), cancellation.Token);
            if (!reply.Success || string.IsNullOrEmpty(reply.ControlPassword))
                throw new IOException("当前房主组件不支持显示总控密码，请关闭房间并更新组件。");
            controlPassword.Text = reply.ControlPassword;
        }));
        accessActions.Children.Add(CopyButton("复制总控密码", controlPassword));
        access.Children.Add(accessActions);
        var accessDisclosure = Disclosure("总控登录", access);
        body.Children.Add(AsyncButton("打开 Web 总控", async () =>
        {
            var reply = await running.CommandAsync(new("openControl"), cancellation.Token);
            if (!reply.Success || !Uri.TryCreate(reply.ControlUrl, UriKind.Absolute, out var url) ||
                url.Scheme != "http" || url.Host != "127.0.0.1") throw new IOException(reply.Error ?? "无法打开本机总控。");
            if (string.IsNullOrEmpty(reply.ControlPassword))
                throw new IOException("当前房主组件不支持显示总控密码，请关闭房间并更新组件。");
            controlPassword.Text = reply.ControlPassword;
            accessDisclosure.IsExpanded = true;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            message.Text = AppLocalization.Literal("若总控要求登录，请复制上方的本次总控密码。");
        }, primary: true));
        body.Children.Add(accessDisclosure);
        body.Children.Add(Text("与服务端相同的总控界面，仅在房主本机开放。", 11));
        var code = Input(running.Invitation.Encode()); code.IsReadOnly = true; code.TextWrapping = TextWrapping.Wrap; code.MinHeight = 90;
        Field(body, "邀请代码", code);
        var invitationActions = new WrapPanel();
        invitationActions.Children.Add(CopyButton("复制邀请代码", code));
        invitationActions.Children.Add(AsyncButton("刷新邀请代码", async () =>
        {
            await CommandAsync(running, new("refreshInvitation"));
            Render();
            message.Text = AppLocalization.Literal("邀请已刷新。旧代码不能用于新加入，已加入成员可以继续比赛和重连。");
        }));
        body.Children.Add(invitationActions);
        if (running.Invitation.SupportsUdp)
        {
            var receipts = new StackPanel();
            receipts.Children.Add(Text("玩家无法直接加入时，将连接回执粘贴到这里，再通知对方继续连接。", 12));
            var receipt = Input(string.Empty); receipt.TextWrapping = TextWrapping.Wrap; receipt.MaxLength = PeerInvitation.MaximumCodeLength;
            Field(receipts, "连接回执", receipt);
            receipts.Children.Add(AsyncButton("添加连接回执", async () =>
            {
                _ = PeerReceipt.Parse(receipt.Text, running.Invitation);
                await CommandAsync(running, new("acceptReceipt", receipt.Text.Trim()));
                receipt.Clear();
                message.Text = AppLocalization.Literal("回执已添加，请对方点击继续连接。");
            }));
            body.Children.Add(Disclosure("通过回执连接", receipts));
        }
        if (!connected())
        {
            var password = Password(); password.Password = hostPassword;
            Field(body, "房间密码", password);
            body.Children.Add(AsyncButton("加入本机房间", () => JoinHostAsync(running, password.Password), primary: true));
        }
        var commands = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (caption, phase) in new[] { ("开始练习", "Practice"), ("开始排位", "Qualifying"), ("准备发车", "Grid"), ("发车", "Countdown") })
            commands.Children.Add(AsyncButton(caption, () => CommandAsync(running, new("phase", phase))));
        commands.Children.Add(AsyncButton("红旗暂停", () => CommandAsync(running, new("flag", "Red"))));
        commands.Children.Add(AsyncButton("绿旗／确认续赛", () => CommandAsync(running, new("flag", "Green"))));
        var advanced = new WrapPanel();
        advanced.Children.Add(AsyncButton("强制发车", async () =>
        {
            if (AppDialog.Show(this, AppLocalization.Literal("跳过赛前检查警告并发车？"), AppLocalization.Literal("强制发车"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await CommandAsync(running, new("phase", "Countdown", true));
        }));
        advanced.Children.Add(AsyncButton("结束本场", () => CommandAsync(running, new("phase", "Finished"))));
        var quickControl = new StackPanel(); quickControl.Children.Add(commands); quickControl.Children.Add(advanced);
        body.Children.Add(Disclosure("快捷比赛控制", quickControl));
        body.Children.Add(AsyncButton("关闭房间", async () =>
        {
            if (AppDialog.Show(this, AppLocalization.Literal("关闭房间会断开所有成员。比赛项目会保留，之后可选择原项目恢复。"), AppLocalization.Literal("关闭房间"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await stop(); hostPassword = string.Empty; Render(); message.Text = AppLocalization.Literal("房间已关闭，比赛项目已保留。");
        }));
        body.Children.Add(Text("关闭此窗口仍会保持房间运行；退出 LazyForza 才会关闭房主组件。", 11));
        AppLocalization.ApplyTo(body);
    }

    private async Task JoinHostAsync(PeerHostProcess running, string password)
    {
        var connection = new PeerConnection(running.Invitation, new PeerEndpoint(IPAddress.Loopback, running.Port));
        await join(Profile(connection, password, saved.DisplayName, saved.IsObserver), cancellation.Token);
        Render();
    }

    private EstateRaceConnectionProfile Profile(PeerConnection connection, string password, string name, bool observer) =>
        new(connection.Origin.AbsoluteUri, password, name.Trim(), saved.ThemeColor, null, null,
            observer ? EstateRaceConnectionRole.Observer : EstateRaceConnectionRole.Driver, connection);

    private async Task CommandAsync(PeerHostProcess running, PeerHostCommand command)
    {
        var result = await running.CommandAsync(command, cancellation.Token);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        message.Text = AppLocalization.Literal("操作已生效。");
    }

    private Button AsyncButton(string caption, Func<Task> action, bool primary = false)
    {
        var button = ActionButton(caption, () => { }, primary);
        button.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; body.IsEnabled = false; message.Text = string.Empty;
            try { await action(); }
            catch (Exception error) { message.Text = AppLocalization.Literal(error.Message); }
            finally { busy = false; body.IsEnabled = true; }
        };
        return button;
    }

    internal Button CopyButton(string caption, TextBox source) => AsyncButton(caption, async () =>
    {
        if (string.IsNullOrEmpty(source.Text)) return;
        if (await ClipboardTransfer.TryCopyAsync(source.Text, cancellation.Token, clipboardWriter))
            message.Text = AppLocalization.Literal("已复制。");
        else
        {
            // AsyncButton disables the form during retries; restore it before requesting
            // focus so Ctrl+C targets this selected text when the operation finishes.
            body.IsEnabled = true;
            source.Focus(); source.SelectAll();
            message.Text = AppLocalization.Literal("剪贴板暂时不可用，内容已选中。请按 Ctrl+C 或稍后重试。");
        }
    });

    private void CloseAfterOperation() { busy = false; Close(); }
    private static void ValidateIdentity(string name, string password)
    {
        if (name.Trim().Length is < 2 or > 20) throw new InvalidOperationException("比赛显示名需要 2–20 个字符。");
        if (password.Length is < 6 or > 128) throw new InvalidOperationException("房间密码需要 6–128 个字符。");
    }
    private static Button ActionButton(string caption, Action action, bool primary = false)
    {
        var button = new Button { Content = AppLocalization.Literal(caption), Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(16, 9, 16, 9), MinHeight = 40 };
        if (primary) { button.SetResourceReference(BackgroundProperty, "AccentSoftBrush"); button.SetResourceReference(BorderBrushProperty, "AccentBrush"); }
        button.Click += (_, _) => action();
        return button;
    }
    private static TextBox Input(string value) => new() { Text = value, MinHeight = 40, Padding = new Thickness(10, 8, 10, 8) };
    private static PasswordBox Password()
    {
        var password = new PasswordBox { MinHeight = 40, Padding = new Thickness(10, 8, 10, 8), MaxLength = 128 };
        password.SetResourceReference(StyleProperty, "SecretEntry");
        return password;
    }
    private static TextBlock Text(string value, double size = 12, string brush = "MutedBrush")
    {
        var text = new TextBlock { Text = AppLocalization.Literal(value), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
        text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return text;
    }
    private static void Field(Panel parent, string label, UIElement control)
    {
        var title = Text(label, 12, "TextBrush"); title.Margin = new Thickness(0, 12, 0, 6);
        parent.Children.Add(title); parent.Children.Add(control);
    }
    private static Border Surface(UIElement child)
    {
        var border = new Border { Child = child, Padding = new Thickness(16), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 12, 0, 6), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "CardBrush"); border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }
    private static Expander Disclosure(string label, UIElement content)
    {
        var expander = new Expander { Header = AppLocalization.Literal(label), Content = content, Margin = new Thickness(0, 10, 0, 6) };
        expander.SetResourceReference(StyleProperty, "SettingsSectionExpander");
        return expander;
    }
    private static Expander NetworkHelp() => Disclosure("连接条件与诊断",
        Text("优先使用同一局域网或双方可达的 IPv6。房主使用公网 IPv4 时需开放 TCP 入站与路由器端口映射。双方都在不可映射的 NAT 后且没有 IPv6 时无法直连，请更换房主或网络。此功能仅同步 LazyForza 赛事，不建立游戏车队连接。", 12));
}
