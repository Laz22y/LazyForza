namespace LazyForza.Storage;

public sealed class DataDirectoryService
{
    public DataDirectoryService(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LazyForza"));
        DatabasePath = Path.Combine(Root, "lazyforza.db");
        RecordingsPath = Path.Combine(Root, "Recordings");
        LogsPath = Path.Combine(Root, "Logs");
        UpdatesPath = Path.Combine(Root, "Updates");
        BackupsPath = Path.Combine(Root, "Backups");
        DiagnosticsPath = Path.Combine(Root, "Diagnostics");
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string RecordingsPath { get; }
    public string LogsPath { get; }
    public string UpdatesPath { get; }
    public string BackupsPath { get; }
    public string DiagnosticsPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RecordingsPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(UpdatesPath);
        Directory.CreateDirectory(BackupsPath);
        Directory.CreateDirectory(DiagnosticsPath);
    }
}
