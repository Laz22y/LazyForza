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
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string RecordingsPath { get; }
    public string LogsPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RecordingsPath);
        Directory.CreateDirectory(LogsPath);
    }
}

