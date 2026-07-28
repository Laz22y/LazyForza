namespace LazyForza.Update;

public sealed class MultiSourceUpdateClient : IDisposable
{
    private readonly UpdateReleaseClientBase gitCode;
    private readonly UpdateReleaseClientBase gitHub;
    private readonly Action<string>? log;

    public MultiSourceUpdateClient(
        UpdateSourceKind preferredSource = UpdateSourceKind.GitCode,
        Action<string>? log = null)
        : this(new GitCodeReleaseClient(), new GitHubReleaseClient(), preferredSource, log)
    {
    }

    public MultiSourceUpdateClient(
        UpdateReleaseClientBase gitCode,
        UpdateReleaseClientBase gitHub,
        UpdateSourceKind preferredSource = UpdateSourceKind.GitCode,
        Action<string>? log = null)
    {
        this.gitCode = gitCode ?? throw new ArgumentNullException(nameof(gitCode));
        this.gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        this.log = log;
        if (gitCode.Source != UpdateSourceKind.GitCode)
            throw new ArgumentException("GitCode 客户端类型不匹配。", nameof(gitCode));
        if (gitHub.Source != UpdateSourceKind.GitHub)
            throw new ArgumentException("GitHub 客户端类型不匹配。", nameof(gitHub));
        PreferredSource = NormalizePreferredSource(preferredSource);
    }

    public UpdateSourceKind PreferredSource { get; set; }

    public async Task<UpdateReleaseInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        var (primary, fallback) = OrderedClients();
        log?.Invoke(
            $"Checking {primary.SourceName} releases. Current version: {currentVersion.ToString(3)}.");
        try
        {
            var release = await primary.CheckForUpdateAsync(currentVersion, cancellationToken)
                .ConfigureAwait(false);
            log?.Invoke(release is null
                ? $"No newer stable {primary.SourceName} release is available."
                : $"{primary.SourceName} release {release.Tag} is available.");
            return release;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception primaryException)
        {
            log?.Invoke(
                $"{primary.SourceName} update check failed: " +
                $"{primaryException.GetType().Name}: {primaryException.Message}");
        }

        log?.Invoke($"Falling back to {fallback.SourceName} releases.");
        try
        {
            var release = await fallback.CheckForUpdateAsync(currentVersion, cancellationToken)
                .ConfigureAwait(false);
            log?.Invoke(release is null
                ? $"No newer stable {fallback.SourceName} release is available."
                : $"{fallback.SourceName} release {release.Tag} is available.");
            return release;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception fallbackException)
        {
            log?.Invoke(
                $"{fallback.SourceName} update check failed: " +
                $"{fallbackException.GetType().Name}: {fallbackException.Message}");
            throw new UpdateException(
                "GitCode 和 GitHub 均无法检查更新，请稍后重试。",
                fallbackException);
        }
    }

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateReleaseInfo release,
        string updatesRoot,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        var selectedClient = release.Source switch
        {
            UpdateSourceKind.GitCode => gitCode,
            UpdateSourceKind.GitHub => gitHub,
            _ => throw new UpdateException($"不支持的更新来源：{release.Source}。")
        };
        var alternateClient = release.Source switch
        {
            UpdateSourceKind.GitCode => gitHub,
            UpdateSourceKind.GitHub => gitCode,
            _ => throw new UpdateException($"不支持的更新来源：{release.Source}。")
        };

        try
        {
            return await selectedClient.DownloadAndPrepareAsync(
                release,
                updatesRoot,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception primaryException)
        {
            log?.Invoke(
                $"{selectedClient.SourceName} update download failed: " +
                $"{primaryException.GetType().Name}: {primaryException.Message}");
            progress?.Report(new UpdateProgress(
                $"{selectedClient.SourceName} 下载或校验失败，正在切换到 {alternateClient.SourceName}…"));

            UpdateReleaseInfo? fallbackRelease;
            try
            {
                fallbackRelease = await alternateClient.CheckForUpdateAsync(
                    new Version(0, 0, 0),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception fallbackCheckException)
            {
                throw new UpdateException(
                    $"{selectedClient.SourceName} 更新下载失败，{alternateClient.SourceName} 备用源也无法取得发行版信息。",
                    fallbackCheckException);
            }

            if (fallbackRelease is null || fallbackRelease.Version != release.Version)
                throw new UpdateException(
                    $"{selectedClient.SourceName} 更新下载失败，{alternateClient.SourceName} 当前发行版不是同一版本 {release.Version.ToString(3)}。",
                    primaryException);

            log?.Invoke($"Retrying release {release.Tag} from {alternateClient.SourceName}.");
            return await alternateClient.DownloadAndPrepareAsync(
                fallbackRelease,
                updatesRoot,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        gitCode.Dispose();
        gitHub.Dispose();
        GC.SuppressFinalize(this);
    }

    private (UpdateReleaseClientBase Primary, UpdateReleaseClientBase Fallback) OrderedClients() =>
        NormalizePreferredSource(PreferredSource) == UpdateSourceKind.GitHub
            ? (gitHub, gitCode)
            : (gitCode, gitHub);

    private static UpdateSourceKind NormalizePreferredSource(UpdateSourceKind source) =>
        source == UpdateSourceKind.GitHub ? UpdateSourceKind.GitHub : UpdateSourceKind.GitCode;
}
