using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

    /// <summary>업데이트 다운로드 후 재시작하며 적용. progress는 0~100(%).
    /// beforeRestart: 재시작 직전 정리 작업 (트레이 아이콘 해제 등 — 안 하면 유령 아이콘이 남는다).</summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, Action<int>? progress = null, Action? beforeRestart = null)
    {
        await _manager.DownloadUpdatesAsync(info, progress);
        await PrewarmAntivirusScanAsync(info);
        beforeRestart?.Invoke();
        StartRestartWatchdog(info.TargetFullRelease.Version.ToString());
        _manager.ApplyUpdatesAndRestart(info);
    }

    /// <summary>백신(V3 Lite 등)이 업데이트로 교체된 "처음 보는" 실행 파일의 실행을
    /// 검사가 끝날 때까지 몇 분씩 차단하는 문제가 있어, 재시작 전에 새 버전 파일들을
    /// 임시 폴더에 미리 풀어 백신이 먼저 검사하도록 유도한다 (해시 캐시 선적재).</summary>
    private static async Task PrewarmAntivirusScanAsync(UpdateInfo info)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var pkg = Path.Combine(root, "packages", info.TargetFullRelease.FileName);
            if (!File.Exists(pkg)) return;

            var dest = Path.Combine(Path.GetTempPath(), "FanboxUpdater-prewarm");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);

            await Task.Run(() =>
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(pkg);
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith("lib/app/", StringComparison.OrdinalIgnoreCase) ||
                        entry.Name.Length == 0) continue;
                    var target = Path.Combine(dest, name["lib/app/".Length..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            });

            // 백신이 방금 쓴 파일들을 검사할 시간을 준다.
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        catch { /* 사전 검사 유도는 실패해도 업데이트 자체에는 지장 없음 */ }
    }

    /// <summary>Update.exe의 자동 재시작이 "액세스 거부"로 실패하는 환경이 있어
    /// (보안 소프트웨어가 프로세스 핸들을 막는 경우 등 — velopack#198),
    /// 외부 cmd 감시자를 먼저 띄워 새 버전 적용 후 앱이 안 떠 있으면 대신 실행한다.
    /// Velopack 재시작이 정상 동작하면 tasklist 검사에 걸려 아무것도 하지 않는다.</summary>
    private static void StartRestartWatchdog(string targetVersion)
    {
        try
        {
            // 실행 중 exe는 <root>\current\FanboxUpdater.exe, 실행 스텁은 <root>\Fanbox Updater.exe
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var stub = Path.Combine(root, "Fanbox Updater.exe");
            var sqVersion = Path.Combine(root, "current", "sq.version");
            if (!File.Exists(stub)) return;

            // timeout은 콘솔 없는 환경에서 실패할 수 있어 ping으로 1초씩 대기한다.
            // find/findstr는 PATH에 GNU find가 앞서는 환경(Git Bash 등)이 있어 절대경로로 고정.
            var script = Path.Combine(Path.GetTempPath(), "FanboxUpdater-restart.cmd");
            File.WriteAllText(script, $"""
                @echo off
                set "sys=%SystemRoot%\System32"
                for /l %%i in (1,1,60) do (
                  "%sys%\findstr.exe" /c:"<version>{targetVersion}" "{sqVersion}" >nul 2>&1 && goto applied
                  "%sys%\ping.exe" -n 2 127.0.0.1 >nul
                )
                :applied
                "%sys%\ping.exe" -n 5 127.0.0.1 >nul
                rem 교체 직후 백신(V3 Lite 등)이 새 실행 파일을 몇 분간 차단할 수 있어
                rem 앱이 뜰 때까지 10초 간격으로 최대 6분간 재시도한다.
                for /l %%j in (1,1,36) do (
                  "%sys%\tasklist.exe" /fi "imagename eq FanboxUpdater.exe" | "%sys%\find.exe" /i "FanboxUpdater.exe" >nul && goto end
                  start "" "{stub}"
                  "%sys%\ping.exe" -n 11 127.0.0.1 >nul
                )
                :end
                """);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                // 앱의 CWD(…\current)를 물려받으면 감시자가 그 폴더를 잠가
                // Update.exe의 current 교체를 막아버린다 — 반드시 앱 폴더 밖으로.
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch { /* 감시자 실패는 치명적이지 않음 — 기존 Velopack 재시작 경로는 그대로 시도된다 */ }
    }
}
