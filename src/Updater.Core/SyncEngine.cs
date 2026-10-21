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

    /// <summary>모든 분류 루트의 폴더와 작가 목록을 매칭한다.</summary>
    public List<FolderMatch> MatchFolders(List<CreatorInfo> creators)
    {
        var result = new List<FolderMatch>();
        var byId = creators.ToDictionary(c => c.CreatorId, StringComparer.OrdinalIgnoreCase);
        // 정규화 키는 작가당 한 번만 계산 (폴더 수 × 작가 수만큼 반복하지 않게)
        var keyed = creators.Select(c => (Creator: c, Id: Normalize(c.CreatorId), Name: Normalize(c.DisplayName))).ToList();

        foreach (var (root, category) in _settings.ExistingRoots())
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
            var hit = keyed.FirstOrDefault(k => k.Id == key || k.Name == key).Creator
                   ?? keyed.FirstOrDefault(k =>
                          SafeContains(k.Id, key) || SafeContains(k.Name, key) ||
                          SafeContains(key, k.Id) || SafeContains(key, k.Name)).Creator;
            result.Add(hit != null
                ? new FolderMatch(folder, hit.CreatorId, hit.DisplayName, category)
                : new FolderMatch(folder, null, null, category));
        }

        // 옵션: 폴더가 없는 팔로우 작가도 자동으로 폴더를 만들어 대상에 포함
        if (_settings.AutoCreateFolders)
        {
            var mappedIds = new HashSet<string>(
                result.Where(m => m.CreatorId != null).Select(m => m.CreatorId!),
                StringComparer.OrdinalIgnoreCase);
            foreach (var c in creators.Where(c => !mappedIds.Contains(c.CreatorId)))
            {
                var category = _settings.CreatorCategories.GetValueOrDefault(c.CreatorId, _settings.DefaultCategory);
                result.Add(new FolderMatch(NameSanitizer.Sanitize(c.DisplayName), c.CreatorId, c.DisplayName, category));
            }
        }
        return result;
    }

    private static bool SafeContains(string a, string b) => b.Length >= 4 && a.Contains(b);

    /// <param name="reDownloadSince">creatorId → 날짜: 이 날짜 이후 게시물은 받은 기록이 있어도 다시 받음</param>
    /// <param name="onlyCreators">지정하면 해당 creatorId만 동기화</param>
    public async Task<SyncSummary> RunAsync(ISiteProvider provider, CancellationToken ct,
        IReadOnlyDictionary<string, DateTime>? reDownloadSince = null,
        HashSet<string>? onlyCreators = null)
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
            .Where(m => onlyCreators == null || onlyCreators.Contains(m.CreatorId!))
            .ToList();
        Log?.Invoke($"대상 작가 {targets.Count}명 최신화 시작");

        int done = 0;
        foreach (var m in targets)
        {
            ct.ThrowIfCancellationRequested();
            var creatorId = m.CreatorId!;
            CreatorProgress?.Invoke(creatorId, done, targets.Count);
            Log?.Invoke($"▶ {m.FolderName} (creatorId: {creatorId}, 작가명: {m.CreatorName})");

            DateTime? since = reDownloadSince != null && reDownloadSince.TryGetValue(creatorId, out var s) ? s : null;
            // 더 처리할 것이 없는 게시물 판정. 지난 동기화를 끝까지 마친 작가는 최신 페이지가 전부 이 상태면
            // 더 오래된 페이지를 조회하지 않는다 (작가당 수십 번의 API 호출을 1~2번으로 줄임).
            // '다시 받기' 중이거나 지난번에 중단·오류가 있었으면 전체 페이지를 다시 훑는다.
            bool IsSettled(RemotePost p) =>
                !p.IsAccessible || (_settings.PaidPostsOnly && p.FeeRequired <= 0) || _state.Contains(creatorId, p.PostId);
            var canStopEarly = since == null && _state.IsComplete(creatorId);

            int newCount = 0, errorsBefore = summary.Errors;
            bool finished = false;
            try
            {
                var posts = await provider.GetPostsAsync(creatorId, ct, canStopEarly ? IsSettled : null);

                foreach (var post in posts)
                {
                    ct.ThrowIfCancellationRequested();
                    // "다시 받기" 지정 범위면 받은 기록을 무시 (이미 있는 파일은 파일 단위로 건너뜀)
                    bool force = since != null && post.PublishedAt >= since;
                    if (!force && _state.Contains(creatorId, post.PostId)) continue;
                    if (!post.IsAccessible) { summary.LockedSkipped++; continue; }
                    // 플랜 구독 게시물만 받기: 무료 공개 글(feeRequired=0)은 제외
                    if (_settings.PaidPostsOnly && post.FeeRequired <= 0) continue;

                    try
                    {
                        if (await DownloadPostAsync(provider, m, post, ct))
                        {
                            _state.Add(creatorId, post.PostId);
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
                finished = true;
            }
            catch (OperationCanceledException)
            {
                summary.Cancelled = true;
                Log?.Invoke("사용자가 중지했습니다.");
                _state.SetComplete(creatorId, false);
                break;
            }
            catch (Exception ex)
            {
                summary.Errors++;
                Log?.Invoke($"  [오류] 작가 {creatorId} 처리 실패: {ex.Message}");
            }
            _state.SetComplete(creatorId, finished && summary.Errors == errorsBefore);
            Log?.Invoke(newCount > 0 ? $"  새 게시물 {newCount}건 저장" : "  새 게시물 없음");
            CreatorDone?.Invoke(creatorId, newCount);
            done++;
        }

        _saveState();
        Log?.Invoke($"완료 — 새 게시물 {summary.NewPosts}건" +
            (summary.LockedSkipped > 0 ? $", 열람 불가 {summary.LockedSkipped}건 건너뜀" : "") +
            (summary.Errors > 0 ? $", 오류 {summary.Errors}건" : ""));
        return summary;
    }

    private async Task<bool> DownloadPostAsync(ISiteProvider provider, FolderMatch m, RemotePost post, CancellationToken ct)
    {
        var content = await provider.GetPostContentAsync(post, ct);
        if (content == null) return false;

        var dirName = NameSanitizer.Sanitize(_settings.FolderNameTemplate
            .Replace("{date}", content.PublishedAt.ToString(_settings.DateFormat))
            .Replace("{title}", content.Title)
            .Replace("{id}", post.PostId)
            .Replace("{creator}", post.CreatorId)).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(dirName)) dirName = $"post_{post.PostId}";

        var items = content.Items.Where(i => _settings.FileTypes.Allows(i.Kind)).ToList();

        // 낱장 일러스트 게시물은 일러스트 루트로 분리 저장.
        // 기준 장수: 작가별 설정 우선 (0 = 분리 안 함), 없으면 전역 설정 ('일러 분리' 체크된 분류에만 적용)
        var categoryRoot = _settings.RootFor(m.Category);
        var root = categoryRoot;
        int threshold = _settings.CreatorIllustThresholds.TryGetValue(post.CreatorId, out var t)
            ? t
            : (_settings.FindCategory(m.Category)?.SplitIllust == true ? _settings.IllustMaxImages : 0);
        if (threshold > 0 && !string.IsNullOrWhiteSpace(_settings.IllustRoot))
        {
            bool hasHeavy = items.Any(i => i.Kind is FileKind.Archive or FileKind.Video);
            int imageCount = items.Count(i => i.Kind == FileKind.Image);
            if (!hasHeavy && imageCount <= threshold)
                root = _settings.IllustRoot;
        }
        var isIllust = root != categoryRoot;
        var postDir = Path.Combine(root, m.FolderName, dirName);
        Directory.CreateDirectory(postDir);

        Log?.Invoke($"  [받는 중] {content.PublishedAt:yyyy-MM-dd} {content.Title}" + (isIllust ? " (일러스트)" : ""));
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

        if (_settings.AutoExtractArchives)
            ExtractArchives(items, postDir);

        // 외부 링크 처리: 링크 카드 해석 → 지원 호스트는 자동 다운로드
        var externalNotes = new List<string>();
        foreach (var link in content.ExternalLinks)
        {
            ct.ThrowIfCancellationRequested();
            var resolved = await ExternalDownloader.ResolveAsync(link, ct);
            bool downloaded = false;
            if (_settings.DownloadExternalLinks)
                downloaded = await ExternalDownloader.TryDownloadAsync(resolved, postDir,
                    msg => Log?.Invoke(msg), ct);
            externalNotes.Add(resolved + (downloaded ? "  [다운로드됨]" : "  [수동 다운로드 필요]"));
            if (!downloaded)
                Log?.Invoke($"  [외부 링크] 자동 다운로드 미지원 — post.txt에 저장: {resolved}");
        }

        if (_settings.SaveText &&
            (!string.IsNullOrWhiteSpace(content.Text) || externalNotes.Count > 0))
        {
            var sb = new StringBuilder();
            sb.AppendLine(content.Title);
            sb.AppendLine(content.PublishedAt.ToString("yyyy-MM-dd HH:mm"));
            if (content.SourceUrl != null) sb.AppendLine(content.SourceUrl);
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(content.Text)) sb.AppendLine(content.Text);
            if (externalNotes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("── 외부 링크 ──");
                foreach (var note in externalNotes) sb.AppendLine(note);
            }
            await File.WriteAllTextAsync(Path.Combine(postDir, "post.txt"), sb.ToString(), ct);
        }

        if (!Directory.EnumerateFileSystemEntries(postDir).Any())
            Directory.Delete(postDir); // 받을 것이 없던 게시물 — 빈 폴더 정리
        return true;
    }

    /// <summary>zip 파일명 인코딩 — UTF-8 플래그가 없는 일본어 zip은 Shift-JIS(cp932)로 해석</summary>
    private static readonly Encoding ZipNameEncoding = GetZipNameEncoding();
    private static Encoding GetZipNameEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }
        catch { return Encoding.UTF8; }
    }

    /// <summary>받은 zip을 게시물 폴더 안에 압축파일명과 같은 하위 폴더로 푼다. RAR·7z는 미지원.</summary>
    private void ExtractArchives(List<DownloadItem> items, string postDir)
    {
        foreach (var item in items.Where(i => i.Kind == FileKind.Archive))
        {
            var archPath = Path.Combine(postDir, NameSanitizer.Sanitize(item.FileName));
            if (!File.Exists(archPath)) continue;
            if (!archPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke($"  [압축] {item.FileName}: zip만 자동 해제를 지원합니다");
                continue;
            }
            var extractDir = Path.Combine(postDir, Path.GetFileNameWithoutExtension(archPath));
            if (Directory.Exists(extractDir)) continue; // 이미 푼 적 있음
            try
            {
                Log?.Invoke($"  [압축 해제] {item.FileName}");
                System.IO.Compression.ZipFile.ExtractToDirectory(archPath, extractDir, ZipNameEncoding, overwriteFiles: true);
                if (_settings.DeleteArchiveAfterExtract) File.Delete(archPath);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"  [압축 해제 실패] {item.FileName}: {ex.Message} — 원본은 그대로 둡니다");
                try
                {
                    if (Directory.Exists(extractDir) && !Directory.EnumerateFileSystemEntries(extractDir).Any())
                        Directory.Delete(extractDir);
                }
                catch { }
            }
        }
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (!char.IsWhiteSpace(ch) && ch is not ('_' or '-' or '+'))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}

public static class NameSanitizer
{
    private static readonly HashSet<char> Invalid = new(Path.GetInvalidFileNameChars());

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }
}
