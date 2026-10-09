using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Updater.App;

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
    public string CreatorId { get => _creatorId; set => Set(ref _creatorId, value); }
    public string CreatorName { get => _creatorName; set => Set(ref _creatorName, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public int DownloadedCount { get => _downloadedCount; set => Set(ref _downloadedCount, value); }
    public string LastResult { get => _lastResult; set => Set(ref _lastResult, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
