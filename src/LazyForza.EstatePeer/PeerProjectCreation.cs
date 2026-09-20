namespace LazyForza.EstatePeer;

/// <summary>Owns only a newly allocated folder. Failed startup never publishes a project manifest.</summary>
public sealed class PeerProjectCreation : IDisposable
{
    private bool committed;
    public Guid Id { get; } = Guid.NewGuid();
    public string Folder { get; }

    public PeerProjectCreation(string projectsRoot)
    {
        var root = Path.GetFullPath(projectsRoot);
        Folder = Path.Combine(root, Id.ToString("N"));
        if (Directory.Exists(Folder)) throw new IOException("项目目录已存在。");
        Directory.CreateDirectory(Folder);
    }

    public void Commit(string manifest)
    {
        var path = Path.Combine(Folder, "project.json");
        File.WriteAllText(path + ".tmp", manifest);
        File.Move(path + ".tmp", path);
        committed = true;
    }

    public void Dispose()
    {
        if (committed || !Directory.Exists(Folder)) return;
        // The GUID path belongs exclusively to this attempt. Never delete an opened project or follow a junction.
        if ((File.GetAttributes(Folder) & FileAttributes.ReparsePoint) != 0) return;
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { } // Unpublished remnants remain invisible if an external process still holds a file.
        catch (UnauthorizedAccessException) { }
    }
}
