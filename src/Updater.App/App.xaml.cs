using System.IO;
using System.Text.Json;
using System.Windows;
using Updater.Core;

namespace Updater.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

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
            var account = AppSettings.Load(SettingsPath).GetAccount(AppSettings.FanboxProvider);
            if (account != null)
            {
                using var browser = new Updater.Providers.Browser.BrowserClient("https://www.fanbox.cc/",
                    new[] { new Updater.Providers.Browser.BrowserCookie("FANBOXSESSID", account.SessionId, ".fanbox.cc") },
                    WebViewDataDir);
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

        // 단일 인스턴스: 이미 실행 중이면 기존 창을 앞으로 띄우고 종료.
        // --multi 는 개발용 — 설치본이 트레이에 떠 있어도 개발 빌드를 따로 띄울 수 있다.
        if (!e.Args.Contains("--multi"))
        {
            _instanceMutex = new Mutex(true, "FanboxUpdater_SingleInstance", out bool isFirstInstance);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "FanboxUpdater_ShowWindow");
            if (!isFirstInstance)
            {
                _showSignal.Set();
                Shutdown(0);
                return;
            }
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var win = new MainWindow();
        if (!AppSettings.Load(SettingsPath).StartMinimized)
            win.Show();

        if (_showSignal != null)
        {
            // 두 번째 실행 시도가 보낸 신호를 받아 창을 앞으로
            var signal = _showSignal;
            var listener = new Thread(() =>
            {
                while (signal.WaitOne())
                    Dispatcher.Invoke(win.ShowFromTray);
            }) { IsBackground = true };
            listener.Start();
        }
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
                // "NasRoot"는 구버전 콘솔 config.json의 실제 키 이름 (이름과 달리 아무 디렉토리나 가능)
                LegacyVideoRoot = root.TryGetProperty("NasRoot", out var nr) ? nr.GetString() ?? "" : "",
                RequestDelayMs = root.TryGetProperty("RequestDelayMs", out var rd) ? rd.GetInt32() : 1000,
                DownloadCoverImage = root.TryGetProperty("DownloadCoverImage", out var dc) && dc.GetBoolean(),
            };
            var session = root.TryGetProperty("FanboxSessionId", out var fs) ? fs.GetString() ?? "" : "";
            settings.Accounts.Add(new AccountSettings { Provider = AppSettings.FanboxProvider, SessionId = session });

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
        using var log = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        void WriteLog(string msg) => log.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}");
        try
        {
            var settings = AppSettings.Load(SettingsPath);
            var state = DownloadState.Load(StatePath);
            var engine = new SyncEngine(settings, state, () => state.Save(StatePath));
            engine.Log += WriteLog;

            // 로그인된 사이트 전부를 한 번에 돌린다 (사이트별로 따로 매칭하면 서로의 작가 폴더를 미매핑으로 보기 때문)
            var providers = new List<ISiteProvider>();
            try
            {
                foreach (var account in settings.ActiveAccounts)
                {
                    var provider = ProviderRegistry.Create(account, settings, WebViewDataDir);
                    if (provider == null) continue;
                    try
                    {
                        await provider.InitializeAsync(CancellationToken.None);
                        providers.Add(provider);
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"[오류] {account.Provider} 로그인 실패: {ex.Message}");
                        provider.Dispose();
                    }
                }
                if (providers.Count > 0)
                    await engine.RunAsync(providers, CancellationToken.None);
            }
            finally
            {
                foreach (var p in providers) p.Dispose();
            }
            settings.Schedule.LastRunAt = DateTime.Now;
            settings.Save(SettingsPath);
        }
        catch (Exception ex)
        {
            WriteLog($"[오류] {ex.Message}");
        }
    }
}
