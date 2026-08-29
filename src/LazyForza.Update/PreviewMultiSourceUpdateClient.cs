namespace LazyForza.Update;

public sealed class PreviewMultiSourceUpdateClient : IDisposable
{
    private static readonly UpdateSemanticVersion EarliestVersion =
        UpdateSemanticVersion.Parse("0.0.0");

    private readonly GitCodePreviewReleaseClient gitCode;
    private readonly GitHubPreviewReleaseClient gitHub;
    private readonly Action<string>? log;

    public PreviewMultiSourceUpdateClient(
        UpdateSourceKind preferredSource = UpdateSourceKind.GitCode,
        Action<string>? log = null)
        : this(
            new GitCodePreviewReleaseClient(),
            new GitHubPreviewReleaseClient(),
            preferredSource,
            log)
    {
    }

    public PreviewMultiSourceUpdateClient(
        GitCodePreviewReleaseClient gitCode,
        GitHubPreviewReleaseClient gitHub,
        UpdateSourceKind preferredSource = UpdateSourceKind.GitCode,
        Action<string>? log = null)
    {
        this.gitCode = gitCode ?? throw new ArgumentNullException(nameof(gitCode));
        this.gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        this.log = log;
        PreferredSource = NormalizePreferredSource(preferredSource);
    }

    public UpdateSourceKind PreferredSource { get; set; }

    public async Task<UpdateReleaseInfo?> CheckForUpdateAsync(
        UpdateSemanticVersion currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        var (primary, fallback) = OrderedSources();
        log?.Invoke($"Checking {primary} preview releases. Current version: {currentVersion}.");
        try
        {
            var release = await CheckSourceAsync(primary, currentVersion, cancellationToken)
                .ConfigureAwait(false);
            log?.Invoke(release is null
                ? $"No newer {primary} preview release is available."
                : $"{primary} preview release {release.Tag} is available.");
            return release;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception primaryException)
        {
            log?.Invoke(
                $"{primary} preview update check failed: " +
                $"{primaryException.GetType().Name}: {primaryException.Message}");
        }

        log?.Invoke($"Falling back to {fallback} preview releases.");
        try
        {
            return await CheckSourceAsync(fallback, currentVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception fallbackException)
        {
            log?.Invoke(
                $"{fallback} preview update check failed: " +
                $"{fallbackException.GetType().Name}: {fallbackException.Message}");
            throw new UpdateException(
                "GitCode 和 GitHub 均无法检查预览版更新，请稍后重试。",
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
        var alternate = release.Source == UpdateSourceKind.GitCode
            ? UpdateSourceKind.GitHub
            : UpdateSourceKind.GitCode;
        try
        {
            return await Client(release.Source).DownloadAndPrepareAsync(
                release,
                updatesRoot,
                progress,
                UpdatePackageKind.Portable,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception primaryException)
        {
            log?.Invoke(
                $"{release.SourceName} preview download failed: " +
                $"{primaryException.GetType().Name}: {primaryException.Message}");
            progress?.Report(new UpdateProgress(
                $"{release.SourceName} 下载或校验失败，正在切换到 {SourceName(alternate)}…"));

            UpdateReleaseInfo? fallbackRelease;
            try
            {
                fallbackRelease = await CheckSourceAsync(
                    alternate,
                    EarliestVersion,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception fallbackCheckException)
            {
                throw new UpdateException(
                    $"{release.SourceName} 预览版下载失败，{SourceName(alternate)} 备用源也无法取得发行版信息。",
                    fallbackCheckException);
            }

            if (fallbackRelease is null ||
                !string.Equals(
                    fallbackRelease.ArtifactVersion,
                    release.ArtifactVersion,
                    StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(
                    $"{release.SourceName} 预览版下载失败，{SourceName(alternate)} 当前发行版不是同一版本 {release.ArtifactVersion}。",
                    primaryException);

            return await Client(alternate).DownloadAndPrepareAsync(
                fallbackRelease,
                updatesRoot,
                progress,
                UpdatePackageKind.Portable,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        gitCode.Dispose();
        gitHub.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<UpdateReleaseInfo?> CheckSourceAsync(
        UpdateSourceKind source,
        UpdateSemanticVersion currentVersion,
        CancellationToken cancellationToken) =>
        source == UpdateSourceKind.GitHub
            ? gitHub.CheckForUpdateAsync(currentVersion, cancellationToken)
            : gitCode.CheckForUpdateAsync(currentVersion, cancellationToken);

    private UpdateReleaseClientBase Client(UpdateSourceKind source) =>
        source == UpdateSourceKind.GitHub ? gitHub : gitCode;

    private (UpdateSourceKind Primary, UpdateSourceKind Fallback) OrderedSources() =>
        NormalizePreferredSource(PreferredSource) == UpdateSourceKind.GitHub
            ? (UpdateSourceKind.GitHub, UpdateSourceKind.GitCode)
            : (UpdateSourceKind.GitCode, UpdateSourceKind.GitHub);

    private static string SourceName(UpdateSourceKind source) =>
        source == UpdateSourceKind.GitHub ? "GitHub" : "GitCode";

    private static UpdateSourceKind NormalizePreferredSource(UpdateSourceKind source) =>
        source == UpdateSourceKind.GitHub ? UpdateSourceKind.GitHub : UpdateSourceKind.GitCode;
}
