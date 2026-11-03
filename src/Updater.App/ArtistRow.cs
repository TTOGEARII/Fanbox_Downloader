using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Updater.Core;

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

/// <summary>사이트 선택 콤보박스 항목 (키 = 프로바이더 이름, 표시 = 사이트 이름)</summary>
public record SiteOption(string Key, string Label);

/// <summary>설정 탭 저장 분류 그리드 한 줄 (편집용 — 저장 시 CategoryRoot로 변환)</summary>
public class CategoryRow : ObservableRow
{
    private bool _selected;
    private string _provider = AppSettings.FanboxProvider;
    private string _name = "";
    private string _path = "";
    private bool _splitIllust;

    /// <summary>삭제 등 일괄 작업용 선택 체크 (저장되지 않음)</summary>
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    /// <summary>이 분류가 속한 사이트 (fanbox / patreon …)</summary>
    public string Provider { get => _provider; set => Set(ref _provider, value); }
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
    private ObservableCollection<string>? _categoryOptions;

    /// <summary>폴더가 놓인 분류 루트의 사이트 (fanbox / patreon …). 작가 탭은 사이트별로 따로 보여준다.</summary>
    public string Provider { get; init; } = AppSettings.FanboxProvider;
    /// <summary>표시용 사이트 이름</summary>
    public string Site => CreatorIds.ProviderLabel(Provider);

    public string Category { get => _category; set => Set(ref _category, value); }
    /// <summary>이 행의 분류 콤보박스 항목 — 같은 사이트의 분류만 (MainWindow가 사이트별 공유 컬렉션을 넣어준다)</summary>
    public ObservableCollection<string>? CategoryOptions { get => _categoryOptions; set => Set(ref _categoryOptions, value); }
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

    /// <summary>수동 매핑·자동 매칭 캐시의 키 (사이트별)</summary>
    public string FolderKey => CreatorIds.FolderKey(Provider, _folderName);

    public string StatusText => _status switch
    {
        MatchStatus.Manual => "수동 매핑",
        MatchStatus.Auto => "자동 매칭",
        MatchStatus.Unmatched => "매칭 안 됨",
        _ => "미확인",
    };
}
