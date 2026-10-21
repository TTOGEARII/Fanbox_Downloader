using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Updater.App;

/// <summary>INotifyPropertyChanged 공통 구현</summary>
public abstract class ObservableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>설정 탭 저장 분류 그리드 한 줄 (편집용 — 저장 시 CategoryRoot로 변환)</summary>
public class CategoryRow : ObservableRow
{
    private bool _selected;
    private string _name = "";
    private string _path = "";
    private bool _splitIllust;

    /// <summary>삭제 등 일괄 작업용 선택 체크 (저장되지 않음)</summary>
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Path { get => _path; set => Set(ref _path, value); }
    public bool SplitIllust { get => _splitIllust; set => Set(ref _splitIllust, value); }
}

/// <summary>폴더 ↔ 작가 매칭 상태</summary>
public enum MatchStatus
{
    /// <summary>아직 작가 목록과 대조하지 않음 (시작 직후, 캐시에 없음)</summary>
    Unknown,
    /// <summary>작가 목록과 대조했지만 일치하는 작가가 없음</summary>
    Unmatched,
    /// <summary>이름/ID로 자동 매칭됨</summary>
    Auto,
    /// <summary>사용자가 직접 매핑함</summary>
    Manual,
}

public class ArtistRow : ObservableRow
{
    private bool _enabled = true;
    private string _folderName = "";
    private string _creatorId = "";
    private string _creatorName = "";
    private MatchStatus _status;
    private int _downloadedCount;
    private string _lastResult = "";
    private string _category = "";
    private string _illustThreshold = "";

    public string Category { get => _category; set => Set(ref _category, value); }
    /// <summary>작가별 일러스트 기준 장수. 빈칸=전역 기본, 0=분리 안 함</summary>
    public string IllustThreshold { get => _illustThreshold; set => Set(ref _illustThreshold, value); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string FolderName { get => _folderName; set => Set(ref _folderName, value); }
    public string CreatorId
    {
        get => _creatorId;
        set { if (Set(ref _creatorId, value)) Raise(nameof(IsMapped)); }
    }
    public string CreatorName { get => _creatorName; set => Set(ref _creatorName, value); }
    public MatchStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) Raise(nameof(StatusText)); }
    }
    public int DownloadedCount { get => _downloadedCount; set => Set(ref _downloadedCount, value); }
    public string LastResult { get => _lastResult; set => Set(ref _lastResult, value); }

    /// <summary>creatorId가 연결되어 있는지 — 작가 그리드의 매핑 표시용</summary>
    public bool IsMapped => !string.IsNullOrEmpty(_creatorId);

    public string StatusText => _status switch
    {
        MatchStatus.Manual => "수동 매핑",
        MatchStatus.Auto => "자동 매칭",
        MatchStatus.Unmatched => "매칭 안 됨",
        _ => "미확인",
    };
}
