using System.IO;
using System.Text.Json;
using System.Windows;
using Updater.Core;

namespace Updater.App;

public partial class App : System.Windows.Application
{
    public static string DataDir { get; private set; } = "";
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string StatePath => Path.Combine(DataDir, "state.json");
    public static string WebViewDataDir => Path.Combine(DataDir, ".webview2");
    public static string LogPath => Path.Combine(DataDir, "app.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DataDir = FindDataDir();
        MigrateLegacyConfig();

        if (e.Args.Length >= 2 && e.Args[0] == "--debug-post")
        {
            // 원시 post.info 응답을 debug_post.json으로 저장 (파싱 문제 진단용)
            var settings = AppSettings.Load(SettingsPath);
            var account = settings.Accounts.FirstOrDefault(a => a.Provider == "fanbox");
            if (account != null)
            {
                using var browser = new Updater.Providers.Fanbox.BrowserClient(account.SessionId, WebViewDataDir);
                var res = await browser.FetchAsync(
                    $"https://api.fanbox.cc/post.info?postId={e.Args[1]}", CancellationToken.None);
                File.WriteAllText(Path.Combine(DataDir, "debug_post.json"),
                    $"status: {res.Status}\n{res.Body}");
            }
            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--sync"))
        {
            // 헤드리스 모드: 창 없이 1회 동기화 후 종료 (작업 스케줄러 연동용)
            await RunHeadlessAsync();
            Shutdown(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var win = new MainWindow();
        if (!AppSettings.Load(SettingsPath).StartMinimized)
            win.Show();
    }

    /// <summary>
    /// 데이터 폴더 결정: exe 위치에서 위로 올라가며 settings.json 등이 있는 폴더(개발/포터블 모드)를 찾고,
    /// 없으면 %AppData%\FanboxUpdater (설치형 모드 — 업데이트해도 경로가 안 바뀜).
    /// </summary>
    private static string FindDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "settings.json")) ||
                File.Exists(Path.Combine(d.FullName, "state.json")) ||
                File.Exists(Path.Combine(d.FullName, "config.json")))
                return d.FullName;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanboxUpdater");
        Directory.CreateDirectory(appData);
        return appData;
    }

    /// <summary>구버전 콘솔용 config.json + mapping.json → settings.json 이전</summary>
    private static void MigrateLegacyConfig()
    {
        if (File.Exists(SettingsPath)) return;
        var configPath = Path.Combine(DataDir, "config.json");
        if (!File.Exists(configPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            var settings = new AppSettings
            {
                NasRoot = root.TryGetProperty("NasRoot", out var nr) ? nr.GetString() ?? "" : "",
                RequestDelayMs = root.TryGetProperty("RequestDelayMs", out var rd) ? rd.GetInt32() : 1000,
                DownloadCoverImage = root.TryGetProperty("DownloadCoverImage", out var dc) && dc.GetBoolean(),
            };
            var session = root.TryGetProperty("FanboxSessionId", out var fs) ? fs.GetString() ?? "" : "";
            settings.Accounts.Add(new AccountSettings { Provider = "fanbox", SessionId = session });

            var mappingPath = Path.Combine(DataDir, "mapping.json");
            if (File.Exists(mappingPath))
            {
                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(mappingPath), JsonDefaults.Options);
                if (mapping != null)
                    foreach (var (k, v) in mapping)
                        if (!string.IsNullOrWhiteSpace(v))
                            settings.Mapping[k] = v;
            }
            settings.Save(SettingsPath);
        }
        catch { /* 이전 실패 시 새 설정으로 시작 */ }
    }

    private static async Task RunHeadlessAsync()
    {
        var log = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        void WriteLog(string msg) => log.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}");
        try
        {
            var settings = AppSettings.Load(SettingsPath);
            var state = DownloadState.Load(StatePath);
            var engine = new SyncEngine(settings, state, () => state.Save(StatePath));
            engine.Log += WriteLog;

            foreach (var account in settings.Accounts.Where(a => a.Enabled))
            {
                using var provider = ProviderRegistry.Create(account, settings, WebViewDataDir);
                if (provider == null) continue;
                await provider.InitializeAsync(CancellationToken.None);
                await engine.RunAsync(provider, CancellationToken.None);
            }
            settings.Schedule.LastRunAt = DateTime.Now;
            settings.Save(SettingsPath);
        }
        catch (Exception ex)
        {
            WriteLog($"[오류] {ex.Message}");
        }
        finally { log.Dispose(); }
    }
}
