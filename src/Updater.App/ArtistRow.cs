using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Updater.App;

/// <summary>설정 탭 저장 분류 그리드 한 줄 (편집용 — 저장 시 CategoryRoot로 변환)</summary>
public class CategoryRow : INotifyPropertyChanged
{
    private string _name = "";
    private string _path = "";
    private bool _splitIllust;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Path { get => _path; set => Set(ref _path, value); }
    public bool SplitIllust { get => _splitIllust; set => Set(ref _splitIllust, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class ArtistRow : INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _folderName = "";
    private string _creatorId = "";
    private string _creatorName = "";
    private string _status = "";
    private int _downloadedCount;
    private string _lastResult = "";
    private string _category = "영상";
    private string _illustThreshold = "";

    public string Category { get => _category; set => Set(ref _category, value); }
    /// <summary>작가별 일러스트 기준 장수. 빈칸=전역 기본, 0=분리 안 함</summary>
    public string IllustThreshold { get => _illustThreshold; set => Set(ref _illustThreshold, value); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string FolderName { get => _folderName; set => Set(ref _folderName, value); }
    public string CreatorId
    {
        get => _creatorId;
        set { if (Set(ref _creatorId, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMapped))); }
    }
    public string CreatorName { get => _creatorName; set => Set(ref _creatorName, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public int DownloadedCount { get => _downloadedCount; set => Set(ref _downloadedCount, value); }
    public string LastResult { get => _lastResult; set => Set(ref _lastResult, value); }

    /// <summary>creatorId가 연결되어 있는지 — 작가 그리드의 매핑 표시용</summary>
    public bool IsMapped => !string.IsNullOrEmpty(_creatorId);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
