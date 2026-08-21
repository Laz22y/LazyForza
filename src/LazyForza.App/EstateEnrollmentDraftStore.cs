using System.IO;
using System.Text.Json;
using LazyForza.Modules.LapAnalysis;

namespace LazyForza.App;

internal sealed class EstateEnrollmentDraftStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string path;

    public EstateEnrollmentDraftStore(string dataRoot)
    {
        path = Path.Combine(Path.GetFullPath(dataRoot), "EstateEnrollmentDraft.json");
    }

    public bool Exists => File.Exists(path);

    public EstateEnrollmentDraft? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<EstateEnrollmentDraft>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("地产环道暂存文件无法读取。可以放弃该暂存后重新录入。", exception);
        }
    }

    public void Save(EstateEnrollmentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("暂存路径无效。");
        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(draft, JsonOptions));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Delete()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
