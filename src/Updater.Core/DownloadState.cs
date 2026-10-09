using System.Text.Json;

namespace Updater.Core;

/// <summary>이미 받은 게시물 기록. creatorId → postId 집합.</summary>
public class DownloadState
{
    public Dictionary<string, HashSet<string>> Downloaded { get; set; } = new();

    public bool Contains(string creatorId, string postId)
        => Downloaded.TryGetValue(creatorId, out var set) && set.Contains(postId);

    public void Add(string creatorId, string postId)
    {
        if (!Downloaded.TryGetValue(creatorId, out var set))
            Downloaded[creatorId] = set = new HashSet<string>();
        set.Add(postId);
    }

    public int CountFor(string creatorId)
        => Downloaded.TryGetValue(creatorId, out var set) ? set.Count : 0;

    public static DownloadState Load(string path)
    {
        if (!File.Exists(path)) return new DownloadState();
        return JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(path), JsonDefaults.Options) ?? new DownloadState();
    }

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
}
