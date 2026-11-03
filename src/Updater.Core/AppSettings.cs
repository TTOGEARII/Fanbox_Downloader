using System.Text.Json;
using System.Text.Json.Serialization;

namespace Updater.Core;

/// <summary>
/// 앱 설정. 사이트에 따라 달라지는 것(쿠키·저장 분류·게시물 필터·다운로드 옵션)은 <see cref="SiteSettings"/>에
/// 사이트별로 들어 있고, 여기에는 공통 설정(폴더 이름 규칙·스케줄·동작)과 creatorId 기준 사전만 남는다.
/// creatorId·폴더 키는 <see cref="CreatorIds"/> 규칙으로 사이트가 구분되므로 사전은 공통이어도 섞이지 않는다.
/// </summary>
public class AppSettings
{
    public const string FanboxProvider = "fanbox";
    public const string PatreonProvider = "patreon";

    /// <summary>사이트별 설정 (키 = 프로바이더 이름)</summary>
    public Dictionary<string, SiteSettings> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int RequestDelayMs { get; set; } = 1000;
    public int MaxParallelDownloads { get; set; } = 3;

    /// <summary>게시물 폴더 이름 규칙. 사용 가능: {date} {title} {id} {creator}</summary>
    public string FolderNameTemplate { get; set; } = "{date} {title}";
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    public ScheduleSettings Schedule { get; set; } = new();

    public bool StartMinimized { get; set; } = false;
    public bool CloseToTray { get; set; } = true;

    /// <summary>작가 탭·설정 탭에서 마지막으로 보던 사이트 (fanbox / patreon …)</summary>
    public string SelectedSite { get; set; } = FanboxProvider;

    /// <summary>폴더 키(<see cref="CreatorIds.FolderKey"/>) → creatorId 수동 매핑</summary>
    public Dictionary<string, string> Mapping { get; set; } = new();
    /// <summary>creatorId → 분류 이름 (없으면 그 사이트의 첫 분류)</summary>
    public Dictionary<string, string> CreatorCategories { get; set; } = new();
    /// <summary>creatorId → 작가명 캐시 (API 조회 시 갱신 — 재시작 후에도 작가명 표시용)</summary>
    public Dictionary<string, string> CreatorNames { get; set; } = new();
    /// <summary>폴더 키 → creatorId 자동 매칭 캐시 (마지막 새로고침 결과 — 시작 시 그리드 표시용, 새로고침마다 재계산)</summary>
    public Dictionary<string, string> AutoMatches { get; set; } = new();
    /// <summary>동기화에서 제외할 creatorId 목록</summary>
    public HashSet<string> DisabledCreators { get; set; } = new();
    /// <summary>작가별 일러스트 기준 장수 재정의. creatorId → 장수 (0 = 분리 안 함, 없으면 사이트 설정)</summary>
    public Dictionary<string, int> CreatorIllustThresholds { get; set; } = new();

    /// <summary>구버전(사이트별 설정 이전) settings.json의 최상위 키들이 여기에 모인다 — Load()에서 Sites로 이전한 뒤 비운다.
    /// 코드에서 직접 쓰지 말 것.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Legacy { get; set; }

    // ---------- 사이트 ----------

    /// <summary>해당 사이트 설정. 없으면 만들어 추가한다.</summary>
    public SiteSettings Site(string provider)
    {
        var key = provider.ToLowerInvariant();
        if (!Sites.TryGetValue(key, out var site))
        {
            Sites[key] = site = new SiteSettings();
            if (key == FanboxProvider) site.DownloadFree = false; // 기존 기본값(플랜 구독 글만) 유지
        }
        site.Provider = key;
        return site;
    }

    /// <summary>사용 가능한(켜져 있고 세션이 입력된) 사이트. 없으면 null.</summary>
    public SiteSettings? ActiveSite(string provider)
    {
        var site = Site(provider);
        return site.Enabled && !string.IsNullOrWhiteSpace(site.SessionId) ? site : null;
    }

    /// <summary>사용 가능한 사이트 전체 (Fanbox → Patreon → 그 외 순)</summary>
    [JsonIgnore]
    public List<SiteSettings> ActiveSites
    {
        get
        {
            var order = new[] { FanboxProvider, PatreonProvider };
            return Sites.Keys
                .OrderBy(k => Array.IndexOf(order, k) is var i && i >= 0 ? i : int.MaxValue)
                .ThenBy(k => k)
                .Select(ActiveSite).OfType<SiteSettings>().ToList();
        }
    }

    /// <summary>프로바이더별 유료/무료 게시물 수신 설정에 따라 이 게시물을 받을지 판정.
    /// 열람 불가 게시물은 여기서 거르지 않는다 (IsAccessible로 따로 처리).</summary>
    public bool AllowsPost(string provider, int feeRequired)
    {
        var site = Site(provider);
        return feeRequired > 0 ? site.DownloadPaid : site.DownloadFree;
    }

    // ---------- 분류 (사이트 설정으로 위임 — 호출부 편의) ----------

    public IEnumerable<CategoryRoot> CategoryRootsFor(string provider) => Site(provider).Categories;
    public CategoryRoot? FindCategory(string provider, string name) => Site(provider).FindCategory(name);
    public string DefaultCategory(string provider) => Site(provider).DefaultCategory;
    public string RootFor(string provider, string category) => Site(provider).RootFor(category);

    /// <summary>경로가 지정된 분류 루트 (같은 사이트에서 같은 경로를 쓰는 분류는 한 번만). 존재하지 않는 경로는 missing에 담긴다.</summary>
    /// <param name="provider">null이면 모든 사이트</param>
    public List<(string Root, string Category, string Provider)> ExistingRoots(string? provider = null, List<string>? missing = null)
    {
        var roots = new List<(string, string, string)>();
        foreach (var (key, site) in Sites.OrderBy(kv => kv.Key == FanboxProvider ? 0 : kv.Key == PatreonProvider ? 1 : 2).ThenBy(kv => kv.Key))
        {
            if (provider != null && !string.Equals(key, provider, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (root, category) in site.ExistingRoots(missing))
                roots.Add((root, category, key.ToLowerInvariant()));
        }
        return roots;
    }

    // ---------- 저장/로드 ----------

    public static AppSettings Load(string path)
    {
        var s = File.Exists(path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options) ?? new AppSettings()
            : new AppSettings();
        var topLegacy = s.Legacy; // MigrateLegacy가 비우기 전에 보관 (한때 공통이던 평평 저장 옵션 이전용)
        s.MigrateLegacy();
        foreach (var (key, site) in s.Sites) site.Provider = key.ToLowerInvariant();
        s.Site(FanboxProvider); // 항상 존재
        // 한때 공통 설정이었던 평평 저장 옵션(FlatFiles 등)이 최상위에 남아 있으면 모든 사이트에 같은 값으로 넣어준다
        if (topLegacy != null && topLegacy.TryGetValue("FlatFiles", out var ff))
        {
            foreach (var site in s.Sites.Values)
            {
                site.FlatFiles = ff.ValueKind == JsonValueKind.True;
                if (topLegacy.TryGetValue("FlatFileNameTemplate", out var t) && t.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(t.GetString()))
                    site.FlatFileNameTemplate = t.GetString()!;
                if (topLegacy.TryGetValue("FlatIllustOnly", out var io))
                    site.FlatIllustOnly = io.ValueKind == JsonValueKind.True;
            }
        }
        return s;
    }

    /// <summary>구버전 최상위 설정(Accounts, CategoryRoots, NasRoot/MangaRoot, PaidPostsOnly, FileTypes …)을 사이트별 설정으로 이전</summary>
    private void MigrateLegacy()
    {
        var legacy = Legacy;
        Legacy = null;
        if (legacy == null || legacy.Count == 0) return;
        if (Sites.Count > 0) return; // 이미 새 형식 — 남은 구키는 버린다

        JsonElement? Get(string name) => legacy.TryGetValue(name, out var v) ? v : null;
        bool Bool(string name, bool fallback) => Get(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : fallback;
        string Str(string name) => Get(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
        int Int(string name, int fallback) => Get(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : fallback;

        var fanbox = Site(FanboxProvider);
        // 계정
        if (Get("Accounts") is { ValueKind: JsonValueKind.Array } accounts)
            foreach (var a in accounts.EnumerateArray())
            {
                var provider = a.TryGetProperty("Provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                if (provider.Length == 0) continue;
                var site = Site(provider);
                site.SessionId = a.TryGetProperty("SessionId", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() ?? "" : "";
                site.Enabled = !a.TryGetProperty("Enabled", out var en) || en.ValueKind != JsonValueKind.False;
            }
        // 분류 (CategoryRoots에 Provider가 있으면 그 사이트로, 없으면 fanbox)
        if (Get("CategoryRoots") is { ValueKind: JsonValueKind.Array } cats)
            foreach (var c in cats.EnumerateArray())
            {
                var provider = c.TryGetProperty("Provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                var site = Site(provider.Length == 0 ? FanboxProvider : provider);
                site.Categories.Add(new CategoryRoot
                {
                    Name = c.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                    Path = c.TryGetProperty("Path", out var pa) ? pa.GetString() ?? "" : "",
                    SplitIllust = c.TryGetProperty("SplitIllust", out var si) && si.ValueKind == JsonValueKind.True,
                });
            }
        // 더 오래된 고정 루트 (NasRoot/MangaRoot)
        if (fanbox.Categories.Count == 0)
        {
            if (Str("NasRoot") is { Length: > 0 } nas) fanbox.Categories.Add(new CategoryRoot { Name = "영상", Path = nas });
            if (Str("MangaRoot") is { Length: > 0 } manga)
                fanbox.Categories.Add(new CategoryRoot { Name = "만화", Path = manga, SplitIllust = Bool("SplitIllustrations", false) });
        }
        // 다운로드 옵션 — 모두 fanbox로 (Patreon 전용 두 값만 patreon으로)
        fanbox.IllustRoot = Str("IllustRoot");
        fanbox.IllustMaxImages = Int("IllustMaxImages", 4);
        fanbox.DownloadExternalLinks = Bool("DownloadExternalLinks", true);
        fanbox.AutoExtractArchives = Bool("AutoExtractArchives", false);
        fanbox.DeleteArchiveAfterExtract = Bool("DeleteArchiveAfterExtract", false);
        fanbox.SaveText = Bool("SaveText", true);
        fanbox.DownloadCoverImage = Bool("DownloadCoverImage", false);
        fanbox.AutoCreateFolders = Bool("AutoCreateFolders", false);
        fanbox.DownloadPaid = true;
        fanbox.DownloadFree = !Bool("PaidPostsOnly", true);
        if (Get("FileTypes") is { ValueKind: JsonValueKind.Object } ft)
            fanbox.FileTypes = JsonSerializer.Deserialize<FileTypeFilter>(ft.GetRawText(), JsonDefaults.Options) ?? new();
        if (legacy.ContainsKey("PatreonDownloadPaid") || legacy.ContainsKey("PatreonDownloadFree"))
        {
            var patreon = Site(PatreonProvider);
            patreon.DownloadPaid = Bool("PatreonDownloadPaid", true);
            patreon.DownloadFree = Bool("PatreonDownloadFree", false);
        }
        if (fanbox.Categories.Count == 0) fanbox.Categories.Add(new CategoryRoot { Name = "영상" });
    }

    public void Save(string path)
        => JsonDefaults.WriteAtomic(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
}

/// <summary>사이트 하나의 설정 — 로그인 쿠키, 저장 분류, 게시물 필터, 다운로드 옵션</summary>
public class SiteSettings
{
    /// <summary>프로바이더 이름 (Sites 사전의 키 — 로드 시 채워짐)</summary>
    [JsonIgnore]
    public string Provider { get; set; } = "";

    /// <summary>로그인 세션 쿠키 값 — fanbox: FANBOXSESSID, patreon: session_id</summary>
    public string SessionId { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>분류별 저장 루트 목록. 첫 항목이 이 사이트의 기본 분류.</summary>
    public List<CategoryRoot> Categories { get; set; } = new();
    /// <summary>일러스트 분리 저장 루트 (낱장 일러스트 게시물용). 비우면 분리 안 함</summary>
    public string IllustRoot { get; set; } = "";
    /// <summary>이미지가 이 장수 이하이고 영상/압축파일이 없으면 일러스트로 분류 (사이트 기본값)</summary>
    public int IllustMaxImages { get; set; } = 4;

    /// <summary>유료(후원/플랜 구독으로 열람하는) 게시물을 받는다</summary>
    public bool DownloadPaid { get; set; } = true;
    /// <summary>무료(공개) 게시물을 받는다</summary>
    public bool DownloadFree { get; set; } = false;

    /// <summary>true면 게시물마다 폴더를 만들지 않고 작가 폴더에 파일을 바로 저장한다 (파일명은 <see cref="FlatFileNameTemplate"/>)</summary>
    public bool FlatFiles { get; set; } = false;
    /// <summary>FlatFiles일 때 파일 이름 규칙 (확장자는 자동). 사용 가능: {title} {index} {id} {date} {creator}</summary>
    public string FlatFileNameTemplate { get; set; } = "{title}_{index}";
    /// <summary>true면 평평 저장을 일러스트 분리 대상 게시물(IllustRoot로 가는 낱장 일러)에만 적용하고, 나머지 게시물은 폴더로 저장</summary>
    public bool FlatIllustOnly { get; set; } = false;

    public bool SaveText { get; set; } = true;
    public bool DownloadCoverImage { get; set; } = false;
    public bool AutoCreateFolders { get; set; } = false;
    /// <summary>본문의 외부 링크(Dropbox/Google Drive/직접 링크)를 자동 다운로드</summary>
    public bool DownloadExternalLinks { get; set; } = true;
    /// <summary>받은 압축파일(zip)을 게시물 폴더 안에 자동으로 풀기</summary>
    public bool AutoExtractArchives { get; set; } = false;
    /// <summary>압축 해제 성공 후 원본 압축파일 삭제</summary>
    public bool DeleteArchiveAfterExtract { get; set; } = false;
    public FileTypeFilter FileTypes { get; set; } = new();

    /// <summary>이름으로 분류 찾기 (대소문자 무시)</summary>
    public CategoryRoot? FindCategory(string name)
        => Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>기본 분류 이름 (첫 항목)</summary>
    [JsonIgnore]
    public string DefaultCategory => Categories.FirstOrDefault()?.Name ?? "";

    /// <summary>분류에 해당하는 저장 루트 경로. 없으면 경로가 지정된 첫 분류의 루트, 그것도 없으면 빈 문자열.</summary>
    public string RootFor(string category)
    {
        var cat = FindCategory(category);
        if (cat != null && !string.IsNullOrWhiteSpace(cat.Path)) return cat.Path;
        return Categories.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Path))?.Path ?? "";
    }

    /// <summary>경로가 지정된 분류 루트 (같은 경로를 쓰는 분류는 한 번만). 존재하지 않는 경로는 missing에 담긴다.</summary>
    public List<(string Root, string Category)> ExistingRoots(List<string>? missing = null)
    {
        var roots = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in Categories)
        {
            if (string.IsNullOrWhiteSpace(cat.Path)) continue;
            if (!Directory.Exists(cat.Path)) { missing?.Add($"{cat.Name}: {cat.Path}"); continue; }
            if (!seen.Add(Path.GetFullPath(cat.Path))) continue;
            roots.Add((cat.Path, cat.Name));
        }
        return roots;
    }
}

/// <summary>분류 하나 — 이름과 저장 루트 경로 (어느 사이트 것인지는 SiteSettings.Categories 소속으로 정해진다)</summary>
public class CategoryRoot
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>이 분류 작가의 낱장 일러스트 게시물을 IllustRoot로 분리 저장</summary>
    public bool SplitIllust { get; set; }
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
    public const string ModeWeekly = "Weekly";
    public const string ModeMonthly = "Monthly";
    public const string ModeInterval = "Interval";

    public bool Enabled { get; set; } = false;
    /// <summary>"Daily" / "Weekly" / "Monthly" / "Interval"</summary>
    public string Mode { get; set; } = ModeDaily;
    /// <summary>Daily·Weekly·Monthly 모드: 실행 시각 (HH:mm)</summary>
    public string DailyTime { get; set; } = "03:00";
    /// <summary>Weekly 모드: 요일 (0=일요일 … 6=토요일)</summary>
    public int WeeklyDay { get; set; } = 1;
    /// <summary>Monthly 모드: 날짜 (1~31, 그 달에 없는 날이면 말일)</summary>
    public int MonthlyDay { get; set; } = 1;
    /// <summary>Interval 모드: 실행 간격 (시간)</summary>
    public int IntervalHours { get; set; } = 6;
    public DateTime? LastRunAt { get; set; }

    /// <summary>다음 자동 실행 시각. 스케줄이 꺼져 있으면 null. now보다 과거면 '지금 실행해야 함'.</summary>
    public DateTime? NextRunAfter(DateTime now)
    {
        if (!Enabled) return null;
        if (Mode == ModeInterval)
            return (LastRunAt ?? DateTime.MinValue).AddHours(Math.Max(1, IntervalHours));

        // 주기 모드 공통: 가장 최근 예정 시각(prev, now 이전)과 그 다음 예정 시각(next).
        // prev 이후로 한 번도(수동 실행 포함) 돌지 않았으면 prev → '지금 실행', 돌았으면 next.
        var (prev, next) = Occurrences(now);
        return LastRunAt == null || LastRunAt < prev ? prev : next;
    }

    /// <summary>now 기준 직전 예정 시각과 그 다음 예정 시각</summary>
    private (DateTime Prev, DateTime Next) Occurrences(DateTime now)
    {
        if (!TimeOnly.TryParse(DailyTime, out var time)) time = new TimeOnly(3, 0);
        var at = time.ToTimeSpan();
        switch (Mode)
        {
            case ModeWeekly:
            {
                var day = Math.Clamp(WeeklyDay, 0, 6);
                var d = now.Date.AddDays(-(((int)now.DayOfWeek - day + 7) % 7)).Add(at);
                if (d > now) d = d.AddDays(-7);
                return (d, d.AddDays(7));
            }
            case ModeMonthly:
            {
                DateTime InMonth(int year, int month)
                    => new DateTime(year, month, Math.Min(Math.Clamp(MonthlyDay, 1, 31), DateTime.DaysInMonth(year, month))).Add(at);
                var cur = InMonth(now.Year, now.Month);
                if (cur > now)
                {
                    var lastMonth = now.Date.AddMonths(-1);
                    return (InMonth(lastMonth.Year, lastMonth.Month), cur);
                }
                var nextMonth = now.Date.AddMonths(1);
                return (cur, InMonth(nextMonth.Year, nextMonth.Month));
            }
            default:
            {
                var today = now.Date.Add(at);
                return today > now ? (today.AddDays(-1), today) : (today, today.AddDays(1));
            }
        }
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
