using System.Text;

namespace Updater.Core;

public class SyncSummary
{
    public int NewPosts;
    public int LockedSkipped;
    public int Errors;
    public List<string> UnmatchedFolders = new();
    public bool Cancelled;
}

public record FolderMatch(string FolderName, string? CreatorId, string? CreatorName, string Category);

/// <summary>사이트 독립적 동기화 엔진. 폴더 매칭 → 신규 게시물 탐색 → 다운로드.</summary>
public class SyncEngine
{
    public event Action<string>? Log;
    /// <summary>(creatorId, 처리한 작가 수, 전체 작가 수)</summary>
    public event Action<string, int, int>? CreatorProgress;
    /// <summary>creatorId별 결과 요약 (새 게시물 수)</summary>
    public event Action<string, int>? CreatorDone;
    /// <summary>폴더 매칭 결과</summary>
    public event Action<List<FolderMatch>>? Matched;

    private readonly AppSettings _settings;
    private readonly DownloadState _state;
    private readonly Action _saveState;

    public SyncEngine(AppSettings settings, DownloadState state, Action saveState)
    {
        _settings = settings;
        _state = state;
        _saveState = saveState;
    }

    /// <summary>분류에 해당하는 저장 루트 경로</summary>
    public string RootFor(string category)
        => category == "만화" && !string.IsNullOrWhiteSpace(_settings.MangaRoot)
            ? _settings.MangaRoot : _settings.NasRoot;

    private IEnumerable<(string Root, string Category)> Roots()
    {
        if (Directory.Exists(_settings.NasRoot)) yield return (_settings.NasRoot, "영상");
        if (!string.IsNullOrWhiteSpace(_settings.MangaRoot) && Directory.Exists(_settings.MangaRoot)
            && !string.Equals(Path.GetFullPath(_settings.MangaRoot), Path.GetFullPath(_settings.NasRoot),
                StringComparison.OrdinalIgnoreCase))
            yield return (_settings.MangaRoot, "만화");
    }

    /// <summary>영상/만화 양쪽 루트의 폴더와 작가 목록을 매칭한다.</summary>
    public List<FolderMatch> MatchFolders(List<CreatorInfo> creators)
    {
        var result = new List<FolderMatch>();
        var byId = creators.ToDictionary(c => c.CreatorId, StringComparer.OrdinalIgnoreCase);

        foreach (var (root, category) in Roots())
        foreach (var dir in Directory.GetDirectories(root))
        {
            var folder = Path.GetFileName(dir);
            // 1순위: 수동 매핑
            if (_settings.Mapping.TryGetValue(folder, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            {
                result.Add(new FolderMatch(folder, mapped,
                    byId.TryGetValue(mapped, out var c) ? c.DisplayName : mapped, category));
                continue;
            }
            // 2순위: 이름/아이디 자동 매칭 (완전 일치 우선, 부분 일치는 4자 이상일 때만)
            var key = Normalize(folder);
            static bool SafeContains(string a, string b)
                => b.Length >= 4 && a.Contains(b);
            var hit = creators.FirstOrDefault(c =>
                    Normalize(c.CreatorId) == key || Normalize(c.DisplayName) == key)
                ?? creators.FirstOrDefault(c =>
                    SafeContains(Normalize(c.CreatorId), key) || SafeContains(Normalize(c.DisplayName), key) ||
                    SafeContains(key, Normalize(c.CreatorId)) || SafeContains(key, Normalize(c.DisplayName)));
            result.Add(hit != null
                ? new FolderMatch(folder, hit.CreatorId, hit.DisplayName, category)
                : new FolderMatch(folder, null, null, category));
        }

        // 같은 작가가 양쪽 루트에 있으면 수동 분류(CreatorCategories)와 일치하는 쪽 우선
        // 옵션: 폴더가 없는 팔로우 작가도 자동으로 폴더를 만들어 대상에 포함
        if (_settings.AutoCreateFolders)
        {
            var mappedIds = new HashSet<string>(
                result.Where(m => m.CreatorId != null).Select(m => m.CreatorId!),
                StringComparer.OrdinalIgnoreCase);
            foreach (var c in creators.Where(c => !mappedIds.Contains(c.CreatorId)))
            {
                var category = _settings.CreatorCategories.GetValueOrDefault(c.CreatorId, "영상");
                result.Add(new FolderMatch(NameSanitizer.Sanitize(c.DisplayName), c.CreatorId, c.DisplayName, category));
            }
        }
        return result;
    }

    public async Task<SyncSummary> RunAsync(ISiteProvider provider, CancellationToken ct)
    {
        var summary = new SyncSummary();
        Log?.Invoke($"[{provider.Name}] 작가 목록을 가져오는 중...");
        var creators = await provider.GetFollowedCreatorsAsync(ct);
        Log?.Invoke($"[{provider.Name}] 팔로우/후원 작가 {creators.Count}명");

        var matches = MatchFolders(creators);
        Matched?.Invoke(matches);
        summary.UnmatchedFolders = matches.Where(m => m.CreatorId == null).Select(m => m.FolderName).ToList();
        if (summary.UnmatchedFolders.Count > 0)
            Log?.Invoke($"매칭 안 된 폴더 {summary.UnmatchedFolders.Count}개 — 작가 탭에서 creatorId를 입력하면 처리됩니다.");

        var targets = matches
            .Where(m => m.CreatorId != null && !_settings.DisabledCreators.Contains(m.CreatorId!))
            .ToList();
        Log?.Invoke($"대상 작가 {targets.Count}명 최신화 시작");

        int done = 0;
        foreach (var m in targets)
        {
            ct.ThrowIfCancellationRequested();
            CreatorProgress?.Invoke(m.CreatorId!, done, targets.Count);
            Log?.Invoke($"▶ {m.FolderName} (creatorId: {m.CreatorId}, 작가명: {m.CreatorName})");
            int newCount = 0;
            try
            {
                var posts = await provider.GetPostsAsync(m.CreatorId!, ct);
                var artistDir = Path.Combine(RootFor(m.Category), m.FolderName);

                foreach (var post in posts)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_state.Contains(m.CreatorId!, post.PostId)) continue;
                    if (!post.IsAccessible) { summary.LockedSkipped++; continue; }
                    // 플랜 구독 게시물만 받기: 무료 공개 글(feeRequired=0)은 제외
                    if (_settings.PaidPostsOnly && post.FeeRequired <= 0) continue;

                    try
                    {
                        if (await DownloadPostAsync(provider, artistDir, post, ct))
                        {
                            _state.Add(m.CreatorId!, post.PostId);
                            _saveState();
                            newCount++;
                            summary.NewPosts++;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        summary.Errors++;
                        Log?.Invoke($"  [오류] 게시물 {post.PostId}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                summary.Cancelled = true;
                Log?.Invoke("사용자가 중지했습니다.");
                break;
            }
            catch (Exception ex)
            {
                summary.Errors++;
                Log?.Invoke($"  [오류] 작가 {m.CreatorId} 처리 실패: {ex.Message}");
            }
            Log?.Invoke(newCount > 0 ? $"  새 게시물 {newCount}건 저장" : "  새 게시물 없음");
            CreatorDone?.Invoke(m.CreatorId!, newCount);
            done++;
        }

        _saveState();
        Log?.Invoke($"완료 — 새 게시물 {summary.NewPosts}건" +
            (summary.LockedSkipped > 0 ? $", 열람 불가 {summary.LockedSkipped}건 건너뜀" : "") +
            (summary.Errors > 0 ? $", 오류 {summary.Errors}건" : ""));
        return summary;
    }

    private async Task<bool> DownloadPostAsync(ISiteProvider provider, string artistDir, RemotePost post, CancellationToken ct)
    {
        var content = await provider.GetPostContentAsync(post, ct);
        if (content == null) return false;

        var dirName = NameSanitizer.Sanitize(_settings.FolderNameTemplate
            .Replace("{date}", content.PublishedAt.ToString(_settings.DateFormat))
            .Replace("{title}", content.Title)
            .Replace("{id}", post.PostId)
            .Replace("{creator}", post.CreatorId)).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(dirName)) dirName = $"post_{post.PostId}";
        var postDir = Path.Combine(artistDir, dirName);
        Directory.CreateDirectory(postDir);

        Log?.Invoke($"  [받는 중] {content.PublishedAt:yyyy-MM-dd} {content.Title}");

        var items = content.Items.Where(i => _settings.FileTypes.Allows(i.Kind)).ToList();
        if (_settings.DownloadCoverImage && !string.IsNullOrEmpty(content.CoverImageUrl))
            items.Add(new DownloadItem(content.CoverImageUrl!, "cover.jpg", FileKind.Image));

        // 파일 병렬 다운로드 (API 호출과 달리 파일 서버는 병렬 허용)
        using var gate = new SemaphoreSlim(Math.Max(1, _settings.MaxParallelDownloads));
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var dest = Path.Combine(postDir, NameSanitizer.Sanitize(item.FileName));
                if (!File.Exists(dest))
                    await provider.DownloadAsync(item.Url, dest, ct);
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);

        if (_settings.SaveText && !string.IsNullOrWhiteSpace(content.Text))
        {
            var sb = new StringBuilder();
            sb.AppendLine(content.Title);
            sb.AppendLine(content.PublishedAt.ToString("yyyy-MM-dd HH:mm"));
            if (content.SourceUrl != null) sb.AppendLine(content.SourceUrl);
            sb.AppendLine();
            sb.Append(content.Text);
            await File.WriteAllTextAsync(Path.Combine(postDir, "post.txt"), sb.ToString(), ct);
        }

        if (!Directory.EnumerateFileSystemEntries(postDir).Any())
            Directory.Delete(postDir); // 받을 것이 없던 게시물 — 빈 폴더 정리
        return true;
    }

    private static string Normalize(string s)
        => new(s.ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch) && ch != '_' && ch != '-' && ch != '+').ToArray());
}

public static class NameSanitizer
{
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }
}
