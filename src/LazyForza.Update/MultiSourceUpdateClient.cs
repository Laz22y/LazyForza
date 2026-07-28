namespace LazyForza.Update;

public sealed class MultiSourceUpdateClient : IDisposable
{
    private readonly UpdateReleaseClientBase primary;
    private readonly UpdateReleaseClientBase fallback;
    private readonly Action<string>? log;

    public MultiSourceUpdateClient(Action<string>? log = null)
        : this(new GitCodeReleaseClient(), new GitHubReleaseClient(), log)
    {
    }

    public MultiSourceUpdateClient(
        UpdateReleaseClientBase primary,
        UpdateReleaseClientBase fallback,
        Action<string>? log = null)
    {
        this.primary = primary ?? throw new ArgumentNullException(nameof(primary));
        this.fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        this.log = log;
        if (primary.Source != UpdateSourceKind.GitCode)
            throw new ArgumentException("主更新源必须是 GitCode。", nameof(primary));
        if (fallback.Source != UpdateSourceKind.GitHub)
            throw new ArgumentException("备用更新源必须是 GitHub。", nameof(fallback));
    }

    public async Task<UpdateReleaseInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
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
            UpdateSourceKind.GitCode => primary,
            UpdateSourceKind.GitHub => fallback,
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
        catch (Exception primaryException) when (release.Source == UpdateSourceKind.GitCode)
        {
            log?.Invoke(
                $"GitCode update download failed: " +
                $"{primaryException.GetType().Name}: {primaryException.Message}");
            progress?.Report(new UpdateProgress(
                "GitCode 下载或校验失败，正在切换到 GitHub…"));

            UpdateReleaseInfo? fallbackRelease;
            try
            {
                fallbackRelease = await fallback.CheckForUpdateAsync(
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
                    "GitCode 更新下载失败，GitHub 备用源也无法取得发行版信息。",
                    fallbackCheckException);
            }

            if (fallbackRelease is null || fallbackRelease.Version != release.Version)
                throw new UpdateException(
                    $"GitCode 更新下载失败，GitHub 当前发行版不是同一版本 {release.Version.ToString(3)}。",
                    primaryException);

            log?.Invoke($"Retrying release {release.Tag} from GitHub.");
            return await fallback.DownloadAndPrepareAsync(
                fallbackRelease,
                updatesRoot,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        primary.Dispose();
        fallback.Dispose();
        GC.SuppressFinalize(this);
    }
}
