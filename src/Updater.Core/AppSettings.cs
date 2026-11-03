using System.Text.Json;

namespace Updater.Core;

public class AppSettings
{
    public List<AccountSettings> Accounts { get; set; } = new();
    /// <summary>분류별 저장 루트 목록. 설정 탭에서 자유롭게 추가/삭제. 첫 항목이 기본 분류.</summary>
    public List<CategoryRoot> CategoryRoots { get; set; } = new();
    /// <summary>구버전 호환용 — 로드 시 CategoryRoots로 이전됨. 코드에서 직접 사용하지 말 것.
    /// JSON 키는 기존 설정 파일 호환을 위해 "NasRoot"를 유지한다 (이름과 달리 아무 디렉토리나 가능).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("NasRoot")]
    public string LegacyVideoRoot { get; set; } = "";
    /// <summary>구버전 호환용 — 로드 시 CategoryRoots로 이전됨.</summary>
    public string MangaRoot { get; set; } = "";
    /// <summary>일러스트 분리 저장 루트 (낱장 일러스트 게시물용)</summary>
    public string IllustRoot { get; set; } = "";
    /// <summary>구버전 호환용 — 로드 시 만화 분류의 SplitIllust로 이전됨.</summary>
    public bool SplitIllustrations { get; set; } = false;
    /// <summary>이미지가 이 장수 이하이고 영상/압축파일이 없으면 일러스트로 분류 (전역 기본값)</summary>
    public int IllustMaxImages { get; set; } = 4;
    /// <summary>작가별 일러스트 기준 장수 재정의. creatorId → 장수 (0 = 분리 안 함, 없으면 전역 설정)</summary>
    public Dictionary<string, int> CreatorIllustThresholds { get; set; } = new();

    /// <summary>본문의 외부 링크(Dropbox/Google Drive/직접 링크)를 자동 다운로드</summary>
    public bool DownloadExternalLinks { get; set; } = true;
    /// <summary>받은 압축파일(zip)을 게시물 폴더 안에 자동으로 풀기</summary>
    public bool AutoExtractArchives { get; set; } = false;
    /// <summary>압축 해제 성공 후 원본 압축파일 삭제</summary>
    public bool DeleteArchiveAfterExtract { get; set; } = false;
    public int RequestDelayMs { get; set; } = 1000;
    public int MaxParallelDownloads { get; set; } = 3;

    /// <summary>게시물 폴더 이름 규칙. 사용 가능: {date} {title} {id} {creator}</summary>
    public string FolderNameTemplate { get; set; } = "{date} {title}";
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    public bool SaveText { get; set; } = true;
    public bool DownloadCoverImage { get; set; } = false;
    public bool AutoCreateFolders { get; set; } = false;

    /// <summary>[FANBOX] true면 플랜 구독(유료)으로 볼 수 있는 게시물만 받고, 무료 공개 게시물은 건너뛴다.</summary>
    public bool PaidPostsOnly { get; set; } = true;

    /// <summary>[Patreon] 후원자 전용(유료) 게시물을 받는다.</summary>
    public bool PatreonDownloadPaid { get; set; } = true;
    /// <summary>[Patreon] 공개(무료) 게시물을 받는다.</summary>
    public bool PatreonDownloadFree { get; set; } = false;

    /// <summary>프로바이더별 유료/무료 게시물 수신 설정에 따라 이 게시물을 받을지 판정.
    /// 미리보기 등 열람 불가 게시물은 여기서 거르지 않는다 (IsAccessible로 따로 처리).</summary>
    public bool AllowsPost(string provider, int feeRequired)
    {
        bool paid = feeRequired > 0;
        if (string.Equals(provider, PatreonProvider, StringComparison.OrdinalIgnoreCase))
            return paid ? PatreonDownloadPaid : PatreonDownloadFree;
        return paid || !PaidPostsOnly;
    }

    public FileTypeFilter FileTypes { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();

    public bool StartMinimized { get; set; } = false;
    public bool CloseToTray { get; set; } = true;

    /// <summary>폴더명 → creatorId 수동 매핑</summary>
    public Dictionary<string, string> Mapping { get; set; } = new();
    /// <summary>creatorId → 분류 이름 (없으면 첫 분류)</summary>
    public Dictionary<string, string> CreatorCategories { get; set; } = new();
    /// <summary>creatorId → 작가명 캐시 (API 조회 시 갱신 — 재시작 후에도 작가명 표시용)</summary>
    public Dictionary<string, string> CreatorNames { get; set; } = new();
    /// <summary>폴더명 → creatorId 자동 매칭 캐시 (마지막 새로고침 결과 — 시작 시 그리드 표시용, 새로고침마다 재계산)</summary>
    public Dictionary<string, string> AutoMatches { get; set; } = new();
    /// <summary>동기화에서 제외할 creatorId 목록</summary>
    public HashSet<string> DisabledCreators { get; set; } = new();

    public const string FanboxProvider = "fanbox";
    public const string PatreonProvider = "patreon";

    /// <summary>해당 프로바이더 계정 (없으면 null)</summary>
    public AccountSettings? GetAccount(string provider)
        => Accounts.FirstOrDefault(a => string.Equals(a.Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>해당 프로바이더 계정. 없으면 만들어 추가한다.</summary>
    public AccountSettings EnsureAccount(string provider)
    {
        var acc = GetAccount(provider);
        if (acc == null)
        {
            acc = new AccountSettings { Provider = provider };
            Accounts.Add(acc);
        }
        return acc;
    }

    /// <summary>사용 가능한(켜져 있고 세션이 입력된) 계정. 없으면 null.</summary>
    public AccountSettings? ActiveAccount(string provider)
    {
        var acc = GetAccount(provider);
        return acc is { Enabled: true } && !string.IsNullOrWhiteSpace(acc.SessionId) ? acc : null;
    }

    /// <summary>사용 가능한(켜져 있고 세션이 입력된) Fanbox 계정. 없으면 null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public AccountSettings? ActiveFanboxAccount => ActiveAccount(FanboxProvider);

    /// <summary>사용 가능한 계정 전체 (Fanbox → Patreon 순)</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<AccountSettings> ActiveAccounts
        => new[] { FanboxProvider, PatreonProvider }.Select(ActiveAccount).OfType<AccountSettings>().ToList();

    /// <summary>작가 탭에서 마지막으로 보던 사이트 (fanbox / patreon …)</summary>
    public string SelectedSite { get; set; } = FanboxProvider;

    /// <summary>해당 사이트의 분류 목록 (순서 유지)</summary>
    public IEnumerable<CategoryRoot> CategoryRootsFor(string provider)
        => CategoryRoots.Where(c => c.IsFor(provider));

    /// <summary>사이트 안에서 이름으로 분류 찾기 (대소문자 무시). 분류 이름은 사이트가 다르면 겹쳐도 된다.</summary>
    public CategoryRoot? FindCategory(string provider, string name)
        => CategoryRootsFor(provider).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>사이트의 기본 분류 이름 (그 사이트의 첫 항목)</summary>
    public string DefaultCategory(string provider) => CategoryRootsFor(provider).FirstOrDefault()?.Name ?? "";

    /// <summary>사이트의 분류에 해당하는 저장 루트 경로. 없으면 그 사이트에서 경로가 지정된 첫 분류의 루트, 그것도 없으면 빈 문자열.</summary>
    public string RootFor(string provider, string category)
    {
        var cat = FindCategory(provider, category);
        if (cat != null && !string.IsNullOrWhiteSpace(cat.Path)) return cat.Path;
        return CategoryRootsFor(provider).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Path))?.Path ?? "";
    }

    /// <summary>경로가 지정된 분류 루트 (같은 사이트에서 같은 경로를 쓰는 분류는 한 번만). 존재하지 않는 경로는 missing에 담긴다.</summary>
    /// <param name="provider">null이면 모든 사이트</param>
    public List<(string Root, string Category, string Provider)> ExistingRoots(string? provider = null, List<string>? missing = null)
    {
        var roots = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in CategoryRoots)
        {
            if (provider != null && !cat.IsFor(provider)) continue;
            if (string.IsNullOrWhiteSpace(cat.Path)) continue;
            if (!Directory.Exists(cat.Path)) { missing?.Add($"{cat.Name}: {cat.Path}"); continue; }
            if (!seen.Add(cat.Provider.ToLowerInvariant() + "|" + Path.GetFullPath(cat.Path))) continue;
            roots.Add((cat.Path, cat.Name, cat.Provider.ToLowerInvariant()));
        }
        return roots;
    }

    public static AppSettings Load(string path)
    {
        var s = File.Exists(path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options) ?? new AppSettings()
            : new AppSettings();
        s.MigrateLegacyRoots();
        return s;
    }

    /// <summary>구버전 영상/만화 고정 루트 설정을 CategoryRoots로 이전</summary>
    private void MigrateLegacyRoots()
    {
        if (CategoryRoots.Count > 0) return;
        if (!string.IsNullOrWhiteSpace(LegacyVideoRoot))
            CategoryRoots.Add(new CategoryRoot { Name = "영상", Path = LegacyVideoRoot });
        if (!string.IsNullOrWhiteSpace(MangaRoot))
            CategoryRoots.Add(new CategoryRoot { Name = "만화", Path = MangaRoot, SplitIllust = SplitIllustrations });
        if (CategoryRoots.Count == 0)
            CategoryRoots.Add(new CategoryRoot { Name = "영상" });
    }

    public void Save(string path)
        => JsonDefaults.WriteAtomic(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
}

/// <summary>분류 하나 — 사이트, 이름, 저장 루트 경로. 분류는 사이트별로 완전히 분리되어 있다
/// (FANBOX 분류의 폴더는 FANBOX 작가와만, Patreon 분류의 폴더는 Patreon 작가와만 매칭).</summary>
public class CategoryRoot
{
    /// <summary>이 분류가 속한 사이트 (fanbox / patreon …). 구버전 설정에는 없으므로 기본값 fanbox.</summary>
    public string Provider { get; set; } = AppSettings.FanboxProvider;
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    public bool IsFor(string provider) => string.Equals(Provider, provider, StringComparison.OrdinalIgnoreCase);
    /// <summary>이 분류 작가의 낱장 일러스트 게시물을 IllustRoot로 분리 저장</summary>
    public bool SplitIllust { get; set; }
}

public class AccountSettings
{
    public string Provider { get; set; } = "fanbox";
    /// <summary>로그인 세션 쿠키 값 — fanbox: FANBOXSESSID, patreon: session_id</summary>
    public string SessionId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class FileTypeFilter
{
    public bool Images { get; set; } = true;
    public bool Videos { get; set; } = true;
    public bool Archives { get; set; } = true;
    public bool Others { get; set; } = true;

    public bool Allows(FileKind kind) => kind switch
    {
        FileKind.Image => Images,
        FileKind.Video => Videos,
        FileKind.Archive => Archives,
        _ => Others,
    };
}

public class ScheduleSettings
{
    public const string ModeDaily = "Daily";
    public const string ModeInterval = "Interval";

    public bool Enabled { get; set; } = false;
    /// <summary>"Daily" 또는 "Interval"</summary>
    public string Mode { get; set; } = ModeDaily;
    /// <summary>Daily 모드: 실행 시각 (HH:mm)</summary>
    public string DailyTime { get; set; } = "03:00";
    /// <summary>Interval 모드: 실행 간격 (시간)</summary>
    public int IntervalHours { get; set; } = 6;
    public DateTime? LastRunAt { get; set; }

    /// <summary>다음 자동 실행 시각. 스케줄이 꺼져 있으면 null. now보다 과거면 '지금 실행해야 함'.</summary>
    public DateTime? NextRunAfter(DateTime now)
    {
        if (!Enabled) return null;
        if (Mode == ModeInterval)
            return (LastRunAt ?? DateTime.MinValue).AddHours(Math.Max(1, IntervalHours));

        // Daily 모드: 오늘 지정 시각. 이미 그 시각 이후에 실행했거나, 시각이 지났는데 오늘 (수동으로라도) 실행했으면 내일.
        if (!TimeOnly.TryParse(DailyTime, out var time)) time = new TimeOnly(3, 0);
        var today = now.Date.Add(time.ToTimeSpan());
        if (LastRunAt >= today) return today.AddDays(1);
        if (today > now) return today;
        return LastRunAt?.Date == now.Date ? today.AddDays(1) : today;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>임시 파일에 쓴 뒤 교체 — 저장 도중 종료돼도 기존 파일이 반쯤 잘린 채 남지 않게.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
