using System.Text.Json;

namespace Updater.Core;

public class AppSettings
{
    public List<AccountSettings> Accounts { get; set; } = new();
    /// <summary>분류별 저장 루트 목록. 설정 탭에서 자유롭게 추가/삭제. 첫 항목이 기본 분류.</summary>
    public List<CategoryRoot> CategoryRoots { get; set; } = new();
    /// <summary>구버전 호환용 — 로드 시 CategoryRoots로 이전됨. 코드에서 직접 사용하지 말 것.</summary>
    public string NasRoot { get; set; } = "";
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

    /// <summary>true면 플랜 구독(유료)으로 볼 수 있는 게시물만 받고, 무료 공개 게시물은 건너뛴다.</summary>
    public bool PaidPostsOnly { get; set; } = true;

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

    /// <summary>이름으로 분류 찾기 (대소문자 무시)</summary>
    public CategoryRoot? FindCategory(string name)
        => CategoryRoots.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>기본 분류 이름 (첫 항목)</summary>
    public string DefaultCategory => CategoryRoots.FirstOrDefault()?.Name ?? "";

    public static AppSettings Load(string path)
    {
        var s = File.Exists(path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options) ?? new AppSettings()
            : new AppSettings();
        s.MigrateLegacyRoots();
        return s;
    }

    /// <summary>구버전 NasRoot/MangaRoot 설정을 CategoryRoots로 이전</summary>
    private void MigrateLegacyRoots()
    {
        if (CategoryRoots.Count > 0) return;
        if (!string.IsNullOrWhiteSpace(NasRoot))
            CategoryRoots.Add(new CategoryRoot { Name = "영상", Path = NasRoot });
        if (!string.IsNullOrWhiteSpace(MangaRoot))
            CategoryRoots.Add(new CategoryRoot { Name = "만화", Path = MangaRoot, SplitIllust = SplitIllustrations });
        if (CategoryRoots.Count == 0)
            CategoryRoots.Add(new CategoryRoot { Name = "영상" });
    }

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
}

/// <summary>분류 하나 — 이름과 저장 루트 경로</summary>
public class CategoryRoot
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>이 분류 작가의 낱장 일러스트 게시물을 IllustRoot로 분리 저장</summary>
    public bool SplitIllust { get; set; }
}

public class AccountSettings
{
    public string Provider { get; set; } = "fanbox";
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
    public bool Enabled { get; set; } = false;
    /// <summary>"Daily" 또는 "Interval"</summary>
    public string Mode { get; set; } = "Daily";
    /// <summary>Daily 모드: 실행 시각 (HH:mm)</summary>
    public string DailyTime { get; set; } = "03:00";
    /// <summary>Interval 모드: 실행 간격 (시간)</summary>
    public int IntervalHours { get; set; } = 6;
    public DateTime? LastRunAt { get; set; }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
