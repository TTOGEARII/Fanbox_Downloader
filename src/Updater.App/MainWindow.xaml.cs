using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Updater.Core;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace Updater.App;

/// <summary>메인 창. 기능별로 partial 파일에 나뉘어 있다:
/// Settings(설정 탭) · Artists(작가 탭) · Sync(동기화·스케줄·프로바이더) · Update(자동 업데이트).</summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string AppTitle = "Fanbox Updater";
    private const int MaxLogChars = 400_000;

    private readonly AppSettings _settings;
    private readonly DownloadState _state;
    private readonly ObservableCollection<ArtistRow> _artists = new();
    private readonly ObservableCollection<CategoryRow> _categoryRows = new();
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _applyDebounce;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyExit;
    private bool _uiReady;
    private bool _uninstalling;

    /// <summary>사이트별 분류 콤보박스 항목 (분류 그리드 내용과 실시간 동기화). 작가 행은 자기 사이트의 컬렉션을 참조한다.</summary>
    private readonly Dictionary<string, ObservableCollection<string>> _categoriesBySite = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>설정 탭 분류 그리드의 사이트 콤보박스 항목 — 지원 사이트가 늘면 자동으로 늘어난다</summary>
    public List<SiteOption> SiteOptions { get; } =
        ProviderRegistry.SupportedProviders.Select(p => new SiteOption(p, CreatorIds.ProviderLabel(p))).ToList();
    /// <summary>작가 탭에서 현재 보고 있는 사이트 (fanbox / patreon …). 작가·분류·매칭은 사이트별로 완전히 분리된다.</summary>
    private string _site = AppSettings.FanboxProvider;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load(App.SettingsPath);
        _state = DownloadState.Load(App.StatePath);
        if (ProviderRegistry.SupportedProviders.Contains(_settings.SelectedSite, StringComparer.OrdinalIgnoreCase))
            _site = _settings.SelectedSite.ToLowerInvariant();
        GridArtists.ItemsSource = _artists;
        GridCategories.ItemsSource = _categoryRows;
        BuildSiteSwitcher();

        // 기본 정렬: 매핑된 작가 우선, 그 다음 폴더명순 (매핑이 바뀌면 실시간 재정렬)
        var artistView = (ListCollectionView)CollectionViewSource.GetDefaultView(_artists);
        artistView.SortDescriptions.Add(new SortDescription(nameof(ArtistRow.IsMapped), ListSortDirection.Descending));
        artistView.SortDescriptions.Add(new SortDescription(nameof(ArtistRow.FolderName), ListSortDirection.Ascending));
        artistView.IsLiveSorting = true;
        artistView.LiveSortingProperties.Add(nameof(ArtistRow.IsMapped));

        LoadSettingsToUi();
        RefreshCategoryUi();
        SetupTray();
        LoadArtistsFromCache();

        // 30초마다: 스케줄 확인 · 다음 실행 표시 · 유휴 프로바이더 해제 · 주기적 업데이트 확인
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tickTimer.Tick += (_, _) => OnTick();
        _tickTimer.Start();
        UpdateNextRunText();

        TxtVersion.Text = $"v{_updater.CurrentVersion}";
        _ = CheckForUpdatesAsync();

        // 작가명 캐시가 비어 있으면(캐시 유실 등) 시작할 때 한 번 자동 새로고침해 복구한다
        if (_settings.CreatorNames.Count == 0 && _settings.ActiveSites.Count > 0)
            Loaded += (_, _) => BtnRefresh_Click(this, new RoutedEventArgs());

        // 설정 탭 자동 반영: 컨트롤 변경 600ms 뒤 저장·적용 (타이핑 중 과도한 재스캔 방지)
        _applyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _applyDebounce.Tick += (_, _) => { _applyDebounce.Stop(); ApplySettings(interactive: false); };
        WireAutoApply();
        _uiReady = true;

        var args = Environment.GetCommandLineArgs();
        // UI 확인용: --demo-overlay 로 실행하면 업데이트 오버레이를 63%로 표시
        if (args.Contains("--demo-overlay")) ShowUpdateOverlay(63);
        // UI 확인용: --demo-splash 로 실행하면 업데이트 적용 스플래시 창을 10초간 표시
        if (args.Contains("--demo-splash")) UpdateService.ShowApplySplashDemo();

        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 포커스를 잃지 않은 텍스트박스 등 마지막 편집이 유실되지 않게 닫기 전에 반영
        // (제거 중에는 저장하면 방금 지운 설정 폴더가 되살아나므로 건너뛴다)
        _applyDebounce.Stop();
        if (!_uninstalling) ApplySettings(interactive: false);
        if (!_reallyExit && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonTip(1500, AppTitle, "트레이에서 계속 실행 중입니다.", System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        _syncCts?.Cancel();
        ReleaseProvider();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    private void OnTick()
    {
        UpdateNextRunText();
        CheckSchedule();
        ReleaseProviderIfIdle();
        if (DateTime.Now - _lastUpdateCheck > UpdateCheckInterval) _ = CheckForUpdatesAsync();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 입력칸 안에서는 단축키를 가로채지 않는다
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox
            or Wpf.Ui.Controls.PasswordBox) return;
        switch (e.Key)
        {
            case Key.F5 when BtnSync.IsEnabled:
                StartSync(site: _site); e.Handled = true; break;
            case Key.Escape when _syncRunning:
                BtnStop_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.R when Keyboard.Modifiers == ModifierKeys.Control && BtnRefresh.IsEnabled:
                BtnRefresh_Click(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    // ---------- 트레이 ----------

    private void SetupTray()
    {
        System.Drawing.Icon trayIcon;
        try
        {
            using var s = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream;
            trayIcon = new System.Drawing.Icon(s);
        }
        catch { trayIcon = System.Drawing.SystemIcons.Application; }

        _tray = new System.Windows.Forms.NotifyIcon { Icon = trayIcon, Visible = true, Text = AppTitle };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = menu.Items.Add("열기", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add("지금 최신화", null, (_, _) => Dispatcher.Invoke(() => StartSync()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    private void Notify(string text)
        => _tray?.ShowBalloonTip(3000, AppTitle, text, System.Windows.Forms.ToolTipIcon.Info);

    private static void ShowInfo(string text) => MessageBox.Show(text, AppTitle);

    private static void OpenExternal(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { ShowInfo($"열 수 없습니다: {ex.Message}"); }
    }

    // ---------- 로그 ----------

    private void AppendLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        // 화면 로그는 무한정 키우지 않는다 (오래 켜 두는 트레이 앱이라 메모리·렌더링 비용이 쌓인다)
        if (TxtLog.Text.Length > MaxLogChars)
        {
            var cut = TxtLog.Text.IndexOf('\n', MaxLogChars / 4);
            TxtLog.Text = cut < 0 ? "" : TxtLog.Text[(cut + 1)..];
        }
        TxtLog.AppendText(line + Environment.NewLine);
        if (ChkAutoScroll.IsChecked == true) TxtLog.ScrollToEnd();
        try { File.AppendAllText(App.LogPath, line + Environment.NewLine); } catch { }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();

    private void BtnOpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(App.LogPath)) { TxtStatusBar.Text = "아직 로그 파일이 없습니다."; return; }
        OpenExternal(App.LogPath);
    }

    private void BtnOpenDataDir_Click(object sender, RoutedEventArgs e) => OpenExternal(App.DataDir);
}
