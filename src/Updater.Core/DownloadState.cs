using System.Text.Json;

namespace Updater.Core;

/// <summary>이미 받은 게시물 기록. creatorId → postId 집합.</summary>
public class DownloadState
{
    public Dictionary<string, HashSet<string>> Downloaded { get; set; } = new();

    /// <summary>마지막 동기화에서 오류·중지 없이 게시물 목록 끝까지 처리한 작가.
    /// 이 작가는 다음 동기화 때 최신 페이지가 전부 처리된 상태면 더 오래된 페이지 조회를 생략한다.</summary>
    public HashSet<string> CompletedCreators { get; set; } = new();

    /// <summary>평평 저장으로 받은 파일 이름 → 게시물 ID (creatorId별). 제목이 같은 게시물의 파일명이 겹치는지
    /// 판정하는 데 쓴다 (<see cref="FlatNamer"/>). 이 기록이 도입되기 전에 받은 파일은 여기 없다.</summary>
    public Dictionary<string, Dictionary<string, string>> FlatFiles { get; set; } = new();

    /// <summary>그 파일명이 같은 작가의 다른 게시물 것으로 기록돼 있는가</summary>
    public bool IsFlatFileOfOther(string creatorId, string fileName, string postId)
        => FlatFiles.TryGetValue(creatorId, out var map)
           && map.TryGetValue(fileName, out var owner)
           && !string.Equals(owner, postId, StringComparison.Ordinal);

    public void ClaimFlatFiles(string creatorId, string postId, IEnumerable<string> fileNames)
    {
        if (!FlatFiles.TryGetValue(creatorId, out var map))
            FlatFiles[creatorId] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileNames) map[name] = postId;
    }

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
        var state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(path), JsonDefaults.Options) ?? new DownloadState();
        // 파일명 비교는 Windows 파일 시스템처럼 대소문자 무시 — 역직렬화된 사전은 기본 비교자라 다시 만든다
        foreach (var key in state.FlatFiles.Keys.ToList())
            state.FlatFiles[key] = new Dictionary<string, string>(state.FlatFiles[key], StringComparer.OrdinalIgnoreCase);
        return state;
    }

    public void Save(string path)
        => JsonDefaults.WriteAtomic(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
}
