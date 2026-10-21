using System.Text.Json;

namespace Updater.Core;

/// <summary>이미 받은 게시물 기록. creatorId → postId 집합.</summary>
public class DownloadState
{
    public Dictionary<string, HashSet<string>> Downloaded { get; set; } = new();

    /// <summary>마지막 동기화에서 오류·중지 없이 게시물 목록 끝까지 처리한 작가.
    /// 이 작가는 다음 동기화 때 최신 페이지가 전부 처리된 상태면 더 오래된 페이지 조회를 생략한다.</summary>
    public HashSet<string> CompletedCreators { get; set; } = new();

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

    public bool IsComplete(string creatorId) => CompletedCreators.Contains(creatorId);

    public void SetComplete(string creatorId, bool complete)
    {
        if (complete) CompletedCreators.Add(creatorId);
        else CompletedCreators.Remove(creatorId);
    }

    public static DownloadState Load(string path)
    {
        if (!File.Exists(path)) return new DownloadState();
        return JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(path), JsonDefaults.Options) ?? new DownloadState();
    }

    public void Save(string path)
        => JsonDefaults.WriteAtomic(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
}
