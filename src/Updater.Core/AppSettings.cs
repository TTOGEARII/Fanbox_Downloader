using System.Text.Json;

namespace Updater.Core;

public class AppSettings
{
    public List<AccountSettings> Accounts { get; set; } = new();
    /// <summary>영상 작가 저장 루트</summary>
    public string NasRoot { get; set; } = "";
    /// <summary>만화 작가 저장 루트 (비우면 영상 루트 사용)</summary>
    public string MangaRoot { get; set; } = "";
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
    /// <summary>creatorId → 분류 ("만화"만 기록, 없으면 "영상")</summary>
    public Dictionary<string, string> CreatorCategories { get; set; } = new();
    /// <summary>동기화에서 제외할 creatorId 목록</summary>
    public HashSet<string> DisabledCreators { get; set; } = new();

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options) ?? new AppSettings();
    }

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
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
