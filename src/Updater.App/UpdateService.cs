using Velopack;
using Velopack.Sources;

namespace Updater.App;

/// <summary>GitHub Releases에서 새 버전을 확인하고 적용한다 (Velopack).</summary>
public class UpdateService
{
    public const string RepoUrl = "https://github.com/TTOGEARII/Fanbox_Downloader";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));

    /// <summary>설치형(Setup.exe)으로 설치된 경우에만 업데이트 가능</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>새 버전이 있으면 UpdateInfo, 없으면 null</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!_manager.IsInstalled) return null;
        return await _manager.CheckForUpdatesAsync();
    }

    /// <summary>업데이트 다운로드 후 재시작하며 적용</summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        await _manager.DownloadUpdatesAsync(info);
        _manager.ApplyUpdatesAndRestart(info);
    }
}
