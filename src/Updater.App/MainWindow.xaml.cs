using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Updater.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Updater.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private AppSettings _settings;
    private DownloadState _state;
    private readonly UpdateService _updater = new();
    private Velopack.UpdateInfo? _pendingUpdate;
    private readonly ObservableCollection<ArtistRow> _artists = new();
    private CancellationTokenSource? _syncCts;
    private System.Windows.Forms.NotifyIcon? _tray;
    private readonly DispatcherTimer _schedulerTimer;
    private bool _reallyExit;
    private bool _syncRunning;
    private bool _suppressRowEvents;

    public string[] Categories { get; } = { "영상", "만화" };

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load(App.SettingsPath);
        _state = DownloadState.Load(App.StatePath);
        GridArtists.ItemsSource = _artists;

        LoadSettingsToUi();
        SetupTray();
        LoadArtistsFromCache();

        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _schedulerTimer.Tick += (_, _) => CheckSchedule();
        _schedulerTimer.Start();
        UpdateNextRunText();

        TxtVersion.Text = $"v{_updater.CurrentVersion}";
        _ = CheckForUpdatesAsync();

        Closing += (s, e) =>
        {
            if (!_reallyExit && _settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                _tray?.ShowBalloonTip(1500, "Fanbox Updater", "트레이에서 계속 실행 중입니다.", System.Windows.Forms.ToolTipIcon.Info);
            }
            else
            {
                _tray?.Dispose();
                Application.Current.Shutdown();
            }
        };
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

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = trayIcon,
            Visible = true,
            Text = "Fanbox Updater",
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("지금 최신화", null, (_, _) => Dispatcher.Invoke(() => StartSync()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        _syncCts?.Cancel();
        Close();
    }

    // ---------- 설정 UI ----------

    private void LoadSettingsToUi()
    {
        var fanbox = _settings.Accounts.FirstOrDefault(a => a.Provider == "fanbox");
        TxtSession.Text = fanbox?.SessionId ?? "";
        TxtNasRoot.Text = _settings.NasRoot;
        TxtMangaRoot.Text = _settings.MangaRoot;
        TxtIllustRoot.Text = _settings.IllustRoot;
        ChkSplitIllust.IsChecked = _settings.SplitIllustrations;
        TxtIllustMax.Text = _settings.IllustMaxImages.ToString();
        TxtTemplate.Text = _settings.FolderNameTemplate;
        TxtDateFormat.Text = _settings.DateFormat;
        TxtParallel.Text = _settings.MaxParallelDownloads.ToString();
        ChkSaveText.IsChecked = _settings.SaveText;
        ChkCover.IsChecked = _settings.DownloadCoverImage;
        ChkAutoCreate.IsChecked = _settings.AutoCreateFolders;
        ChkPaidOnly.IsChecked = _settings.PaidPostsOnly;
        ChkExternal.IsChecked = _settings.DownloadExternalLinks;
        ChkImages.IsChecked = _settings.FileTypes.Images;
        ChkVideos.IsChecked = _settings.FileTypes.Videos;
        ChkArchives.IsChecked = _settings.FileTypes.Archives;
        ChkOthers.IsChecked = _settings.FileTypes.Others;
        ChkSchedule.IsChecked = _settings.Schedule.Enabled;
        RadDaily.IsChecked = _settings.Schedule.Mode == "Daily";
        RadInterval.IsChecked = _settings.Schedule.Mode == "Interval";
        TxtDailyTime.Text = _settings.Schedule.DailyTime;
        TxtIntervalHours.Text = _settings.Schedule.IntervalHours.ToString();
        ChkStartMin.IsChecked = _settings.StartMinimized;
        ChkCloseTray.IsChecked = _settings.CloseToTray;
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var fanbox = _settings.Accounts.FirstOrDefault(a => a.Provider == "fanbox");
        if (fanbox == null)
        {
            fanbox = new AccountSettings { Provider = "fanbox" };
            _settings.Accounts.Add(fanbox);
        }
        fanbox.SessionId = TxtSession.Text.Trim();
        _settings.NasRoot = TxtNasRoot.Text.Trim();
        _settings.MangaRoot = TxtMangaRoot.Text.Trim();
        _settings.IllustRoot = TxtIllustRoot.Text.Trim();
        _settings.SplitIllustrations = ChkSplitIllust.IsChecked == true;
        _settings.IllustMaxImages = int.TryParse(TxtIllustMax.Text, out var im) ? Math.Clamp(im, 1, 50) : 4;
        _settings.FolderNameTemplate = string.IsNullOrWhiteSpace(TxtTemplate.Text) ? "{date} {title}" : TxtTemplate.Text;
        _settings.DateFormat = string.IsNullOrWhiteSpace(TxtDateFormat.Text) ? "yyyy-MM-dd" : TxtDateFormat.Text;
        _settings.MaxParallelDownloads = int.TryParse(TxtParallel.Text, out var p) ? Math.Clamp(p, 1, 8) : 3;
        _settings.SaveText = ChkSaveText.IsChecked == true;
        _settings.DownloadCoverImage = ChkCover.IsChecked == true;
        _settings.AutoCreateFolders = ChkAutoCreate.IsChecked == true;
        _settings.PaidPostsOnly = ChkPaidOnly.IsChecked == true;
        _settings.DownloadExternalLinks = ChkExternal.IsChecked == true;
        _settings.FileTypes.Images = ChkImages.IsChecked == true;
        _settings.FileTypes.Videos = ChkVideos.IsChecked == true;
        _settings.FileTypes.Archives = ChkArchives.IsChecked == true;
        _settings.FileTypes.Others = ChkOthers.IsChecked == true;
        _settings.Schedule.Enabled = ChkSchedule.IsChecked == true;
        _settings.Schedule.Mode = RadInterval.IsChecked == true ? "Interval" : "Daily";
        _settings.Schedule.DailyTime = TimeOnly.TryParse(TxtDailyTime.Text, out _) ? TxtDailyTime.Text : "03:00";
        _settings.Schedule.IntervalHours = int.TryParse(TxtIntervalHours.Text, out var h) ? Math.Max(1, h) : 6;
        _settings.StartMinimized = ChkStartMin.IsChecked == true;
        _settings.CloseToTray = ChkCloseTray.IsChecked == true;

        _settings.Save(App.SettingsPath);
        UpdateNextRunText();
        TxtStatusBar.Text = "설정을 저장했습니다.";
    }

    // ---------- 작가 목록 ----------

    /// <summary>영상/만화 양쪽 루트의 폴더 + 수동 매핑 기준으로 그리드를 즉시 채운다 (네트워크 없이).</summary>
    private void LoadArtistsFromCache()
    {
        _suppressRowEvents = true;
        _artists.Clear();
        var roots = new List<(string Root, string Category)>();
        if (Directory.Exists(_settings.NasRoot)) roots.Add((_settings.NasRoot, "영상"));
        else TxtStatusBar.Text = $"영상 경로에 접근할 수 없습니다: {_settings.NasRoot}";
        if (!string.IsNullOrWhiteSpace(_settings.MangaRoot) && Directory.Exists(_settings.MangaRoot))
            roots.Add((_settings.MangaRoot, "만화"));

        foreach (var (root, category) in roots)
        foreach (var dir in Directory.GetDirectories(root))
        {
            var folder = Path.GetFileName(dir);
            _settings.Mapping.TryGetValue(folder, out var mapped);
            var row = new ArtistRow
            {
                FolderName = folder,
                Category = category,
                CreatorId = mapped ?? "",
                Status = string.IsNullOrEmpty(mapped) ? "미확인" : "수동 매핑",
                DownloadedCount = string.IsNullOrEmpty(mapped) ? 0 : _state.CountFor(mapped),
                Enabled = string.IsNullOrEmpty(mapped) || !_settings.DisabledCreators.Contains(mapped),
                IllustThreshold = mapped != null && _settings.CreatorIllustThresholds.TryGetValue(mapped, out var th)
                    ? th.ToString() : "",
            };
            HookRow(row);
            _artists.Add(row);
        }
        _suppressRowEvents = false;
        TxtStatusBar.Text = $"폴더 {_artists.Count}개. '작가 목록 새로고침'을 누르면 자동 매칭을 실행합니다.";
    }

    private void HookRow(ArtistRow row)
    {
        row.PropertyChanged += (s, e) =>
        {
            if (_suppressRowEvents) return;
            if (e.PropertyName == nameof(ArtistRow.Enabled)) OnRowEnabledChanged((ArtistRow)s!);
            if (e.PropertyName == nameof(ArtistRow.Category)) OnRowCategoryChanged((ArtistRow)s!);
        };
    }

    private void OnRowEnabledChanged(ArtistRow row)
    {
        if (string.IsNullOrEmpty(row.CreatorId)) return;
        if (row.Enabled) _settings.DisabledCreators.Remove(row.CreatorId);
        else _settings.DisabledCreators.Add(row.CreatorId);
        _settings.Save(App.SettingsPath);
    }

    private void OnRowCategoryChanged(ArtistRow row)
    {
        if (!string.IsNullOrEmpty(row.CreatorId))
        {
            if (row.Category == "만화") _settings.CreatorCategories[row.CreatorId] = "만화";
            else _settings.CreatorCategories.Remove(row.CreatorId);
            _settings.Save(App.SettingsPath);
        }

        // 기존 폴더를 새 분류의 루트로 이동할지 묻기
        var oldRoot = row.Category == "만화" ? _settings.NasRoot : _settings.MangaRoot;
        var newRoot = row.Category == "만화" ? _settings.MangaRoot : _settings.NasRoot;
        if (string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot) ||
            string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)) return;
        var oldPath = Path.Combine(oldRoot, row.FolderName);
        var newPath = Path.Combine(newRoot, row.FolderName);
        if (!Directory.Exists(oldPath) || Directory.Exists(newPath)) return;

        var answer = MessageBox.Show(
            $"'{row.FolderName}' 폴더를 {row.Category} 저장 경로로 이동할까요?\n\n{oldPath}\n→ {newPath}",
            "폴더 이동", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                Directory.Move(oldPath, newPath);
                TxtStatusBar.Text = $"'{row.FolderName}' 폴더를 이동했습니다.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"폴더 이동 실패: {ex.Message}", "Fanbox Updater");
            }
        }
    }

    private void ChkAllSync_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = ((System.Windows.Controls.CheckBox)sender).IsChecked == true;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_artists);
        _suppressRowEvents = true;
        int count = 0;
        foreach (var row in view.Cast<object>().OfType<ArtistRow>())
        {
            row.Enabled = isChecked;
            count++;
            if (string.IsNullOrEmpty(row.CreatorId)) continue;
            if (isChecked) _settings.DisabledCreators.Remove(row.CreatorId);
            else _settings.DisabledCreators.Add(row.CreatorId);
        }
        _suppressRowEvents = false;
        _settings.Save(App.SettingsPath);
        TxtStatusBar.Text = $"{count}개 작가 동기화 {(isChecked ? "전체 선택" : "전체 해제")}";
    }

    private void CmbFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GridArtists == null) return;
        var filter = (CmbFilter.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "전체";
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_artists);
        view.Filter = filter switch
        {
            "영상" => o => ((ArtistRow)o).Category == "영상",
            "만화" => o => ((ArtistRow)o).Category == "만화",
            "매칭 안 됨" => o => string.IsNullOrEmpty(((ArtistRow)o).CreatorId),
            _ => null,
        };
    }

    private void GridArtists_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        // creatorId / 일러 기준 칸 수동 입력 → 설정 저장
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Row.Item is not ArtistRow row) return;
            var id = row.CreatorId.Trim();
            if (string.IsNullOrEmpty(id))
                _settings.Mapping.Remove(row.FolderName);
            else
            {
                _settings.Mapping[row.FolderName] = id;
                if (row.Status is "미확인" or "매칭 안 됨") row.Status = "수동 매핑";
                row.DownloadedCount = _state.CountFor(id);
            }

            // 일러 기준 장수 (빈칸=전역 기본, 0=분리 안 함)
            if (!string.IsNullOrEmpty(id))
            {
                var raw = row.IllustThreshold.Trim();
                if (string.IsNullOrEmpty(raw))
                    _settings.CreatorIllustThresholds.Remove(id);
                else if (int.TryParse(raw, out var th) && th >= 0)
                    _settings.CreatorIllustThresholds[id] = Math.Min(th, 100);
                else
                    row.IllustThreshold = ""; // 잘못된 입력은 초기화
            }
            _settings.Save(App.SettingsPath);
        }, DispatcherPriority.Background);
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_syncRunning) return;
        var account = _settings.Accounts.FirstOrDefault(a => a.Provider == "fanbox" && a.Enabled);
        if (account == null || string.IsNullOrWhiteSpace(account.SessionId))
        {
            MessageBox.Show("설정 탭에서 FANBOXSESSID 쿠키를 먼저 입력하세요.", "Fanbox Updater");
            return;
        }
        BtnRefresh.IsEnabled = false;
        TxtStatus.Text = "작가 목록 가져오는 중...";
        try
        {
            using var provider = ProviderRegistry.Create(account, _settings, App.WebViewDataDir)!;
            await provider.InitializeAsync(CancellationToken.None);
            var creators = await provider.GetFollowedCreatorsAsync(CancellationToken.None);
            var engine = new SyncEngine(_settings, _state, SaveState);
            ApplyMatches(engine.MatchFolders(creators));
            TxtStatus.Text = $"작가 매칭 완료 — 팔로우/후원 {creators.Count}명";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "작가 목록 조회 실패";
            AppendLog($"[오류] {ex.Message}");
        }
        finally { BtnRefresh.IsEnabled = true; }
    }

    private void ApplyMatches(List<FolderMatch> matches)
    {
        _suppressRowEvents = true;
        foreach (var m in matches)
        {
            var row = _artists.FirstOrDefault(r => r.FolderName == m.FolderName && r.Category == m.Category)
                   ?? _artists.FirstOrDefault(r => r.FolderName == m.FolderName);
            if (row == null)
            {
                row = new ArtistRow { FolderName = m.FolderName, Category = m.Category };
                HookRow(row);
                _artists.Add(row);
            }
            row.Category = m.Category;
            if (m.CreatorId != null)
            {
                row.CreatorId = m.CreatorId;
                row.CreatorName = m.CreatorName ?? "";
                row.Status = _settings.Mapping.ContainsKey(m.FolderName) ? "수동 매핑" : "자동 매칭";
                row.DownloadedCount = _state.CountFor(m.CreatorId);
                row.Enabled = !_settings.DisabledCreators.Contains(m.CreatorId);
                row.IllustThreshold = _settings.CreatorIllustThresholds.TryGetValue(m.CreatorId, out var th)
                    ? th.ToString() : "";
            }
            else
            {
                row.Status = "매칭 안 됨";
            }
        }
        _suppressRowEvents = false;
    }

    // ---------- 동기화 ----------

    private void BtnSync_Click(object sender, RoutedEventArgs e) => StartSync();

    private async void StartSync()
    {
        if (_syncRunning) return;
        var account = _settings.Accounts.FirstOrDefault(a => a.Provider == "fanbox" && a.Enabled);
        if (account == null || string.IsNullOrWhiteSpace(account.SessionId))
        {
            MessageBox.Show("설정 탭에서 FANBOXSESSID 쿠키를 먼저 입력하세요.", "Fanbox Updater");
            return;
        }

        _syncRunning = true;
        _syncCts = new CancellationTokenSource();
        BtnSync.IsEnabled = false;
        BtnStop.IsEnabled = true;
        Progress.Value = 0;
        TxtStatus.Text = "동기화 시작...";

        var engine = new SyncEngine(_settings, _state, SaveState);
        engine.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
        engine.Matched += matches => Dispatcher.BeginInvoke(() => ApplyMatches(matches));
        engine.CreatorProgress += (creatorId, done, total) => Dispatcher.BeginInvoke(() =>
        {
            Progress.Value = total == 0 ? 0 : done * 100.0 / total;
            var row = _artists.FirstOrDefault(r => r.CreatorId == creatorId);
            TxtStatus.Text = $"동기화 중 ({done + 1}/{total}) — {row?.FolderName ?? creatorId}";
        });
        engine.CreatorDone += (creatorId, newCount) => Dispatcher.BeginInvoke(() =>
        {
            var row = _artists.FirstOrDefault(r => r.CreatorId == creatorId);
            if (row != null)
            {
                row.LastResult = newCount > 0 ? $"새 게시물 {newCount}건 ({DateTime.Now:HH:mm})" : $"변경 없음 ({DateTime.Now:HH:mm})";
                row.DownloadedCount = _state.CountFor(creatorId);
            }
        });

        try
        {
            using var provider = ProviderRegistry.Create(account, _settings, App.WebViewDataDir)!;
            TxtStatus.Text = "브라우저 엔진 초기화 중...";
            await provider.InitializeAsync(_syncCts.Token);
            var summary = await engine.RunAsync(provider, _syncCts.Token);

            Progress.Value = 100;
            TxtStatus.Text = summary.Cancelled
                ? $"중지됨 — 새 게시물 {summary.NewPosts}건 저장"
                : $"완료 — 새 게시물 {summary.NewPosts}건" +
                  (summary.LockedSkipped > 0 ? $", 열람 불가 {summary.LockedSkipped}건" : "") +
                  (summary.Errors > 0 ? $", 오류 {summary.Errors}건" : "");
            _tray?.ShowBalloonTip(3000, "Fanbox Updater", TxtStatus.Text, System.Windows.Forms.ToolTipIcon.Info);

            _settings.Schedule.LastRunAt = DateTime.Now;
            _settings.Save(App.SettingsPath);
            UpdateNextRunText();
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "중지됨";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "동기화 실패";
            AppendLog($"[오류] {ex.Message}");
            MessageBox.Show(ex.Message, "동기화 실패");
        }
        finally
        {
            _syncRunning = false;
            BtnSync.IsEnabled = true;
            BtnStop.IsEnabled = false;
            _syncCts?.Dispose();
            _syncCts = null;
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => _syncCts?.Cancel();

    private void SaveState() => _state.Save(App.StatePath);

    // ---------- 스케줄러 ----------

    private void CheckSchedule()
    {
        UpdateNextRunText();
        if (!_settings.Schedule.Enabled || _syncRunning) return;
        var next = GetNextRunTime();
        if (next != null && DateTime.Now >= next)
            StartSync();
    }

    private DateTime? GetNextRunTime()
    {
        var s = _settings.Schedule;
        if (!s.Enabled) return null;
        if (s.Mode == "Interval")
            return (s.LastRunAt ?? DateTime.MinValue).AddHours(s.IntervalHours);

        // Daily 모드
        if (!TimeOnly.TryParse(s.DailyTime, out var time)) time = new TimeOnly(3, 0);
        var today = DateTime.Today.Add(time.ToTimeSpan());
        if (s.LastRunAt >= today) return today.AddDays(1); // 오늘은 이미 실행함
        return today > DateTime.Now ? today : (s.LastRunAt?.Date == DateTime.Today ? today.AddDays(1) : today);
    }

    private void UpdateNextRunText()
    {
        var next = GetNextRunTime();
        TxtNextRun.Text = next == null ? "" :
            next <= DateTime.Now ? "자동 실행: 곧 시작" : $"다음 자동 실행: {next:MM-dd HH:mm}";
    }

    // ---------- 자동 업데이트 ----------

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            _pendingUpdate = await _updater.CheckAsync();
            if (_pendingUpdate != null)
            {
                var ver = _pendingUpdate.TargetFullRelease.Version;
                BtnUpdate.Content = $"⬆ v{ver} 업데이트";
                BtnUpdate.Visibility = Visibility.Visible;
                AppendLog($"새 버전 v{ver}이(가) 있습니다. 상단 업데이트 버튼으로 설치하세요.");
            }
        }
        catch { /* 오프라인/개발 모드 등 — 조용히 무시 */ }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        if (_syncRunning)
        {
            MessageBox.Show("동기화가 끝난 뒤 업데이트하세요.", "Fanbox Updater");
            return;
        }
        BtnUpdate.IsEnabled = false;
        BtnUpdate.Content = "다운로드 중...";
        try
        {
            _reallyExit = true;
            await _updater.DownloadAndApplyAsync(_pendingUpdate); // 재시작하며 적용
        }
        catch (Exception ex)
        {
            BtnUpdate.IsEnabled = true;
            BtnUpdate.Content = "업데이트";
            _reallyExit = false;
            MessageBox.Show($"업데이트 실패: {ex.Message}", "Fanbox Updater");
        }
    }

    // ---------- 로그 ----------

    private void AppendLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();
        try { File.AppendAllText(App.LogPath, line + Environment.NewLine); } catch { }
    }
}
