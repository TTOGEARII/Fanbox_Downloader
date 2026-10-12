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

    /// <summary>작가 그리드의 분류 콤보박스 항목 (분류 그리드 내용과 실시간 동기화)</summary>
    public ObservableCollection<string> Categories { get; } = new();
    private readonly ObservableCollection<CategoryRow> _categoryRows = new();
    /// <summary>마지막으로 가져온 팔로우/후원 작가 목록 (수동 매핑 선택용)</summary>
    private List<CreatorInfo> _creatorCache = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load(App.SettingsPath);
        _state = DownloadState.Load(App.StatePath);
        GridArtists.ItemsSource = _artists;
        GridCategories.ItemsSource = _categoryRows;

        LoadSettingsToUi();
        RefreshCategoryUi();
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
        _categoryRows.Clear();
        foreach (var c in _settings.CategoryRoots)
        {
            var row = new CategoryRow { Name = c.Name, Path = c.Path, SplitIllust = c.SplitIllust };
            HookCategoryRow(row);
            _categoryRows.Add(row);
        }
        TxtIllustRoot.Text = _settings.IllustRoot;
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

        // 분류 그리드 → 설정 (편집 중인 셀 커밋 후 검증)
        GridCategories.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        var cats = _categoryRows
            .Select(r => new CategoryRoot { Name = r.Name.Trim(), Path = r.Path.Trim(), SplitIllust = r.SplitIllust })
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .ToList();
        if (cats.Count == 0)
        {
            MessageBox.Show("저장 분류를 최소 1개 입력하세요.", "Fanbox Updater");
            return;
        }
        if (cats.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cats.Count)
        {
            MessageBox.Show("분류 이름이 중복되었습니다.", "Fanbox Updater");
            return;
        }
        _settings.CategoryRoots = cats;

        _settings.IllustRoot = TxtIllustRoot.Text.Trim();
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
        RefreshCategoryUi();
        LoadArtistsFromCache();
        UpdateNextRunText();
        TxtStatusBar.Text = "설정을 저장했습니다.";
    }

    /// <summary>분류 콤보박스(작가 그리드·필터)를 분류 그리드 내용으로 갱신 — 분류 입력 즉시 반영</summary>
    private void RefreshCategoryUi()
    {
        Categories.Clear();
        foreach (var r in _categoryRows.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
            if (!Categories.Contains(r.Name)) Categories.Add(r.Name);

        var selected = CmbFilter.SelectedItem as string ?? "전체";
        var items = new List<string> { "전체" };
        items.AddRange(Categories);
        items.Add("매칭 안 됨");
        CmbFilter.ItemsSource = items;
        CmbFilter.SelectedItem = items.Contains(selected) ? selected : "전체";
    }

    private void HookCategoryRow(CategoryRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CategoryRow.Name)) RefreshCategoryUi();
        };
    }

    private void BtnAddCategory_Click(object sender, RoutedEventArgs e)
    {
        var row = new CategoryRow();
        HookCategoryRow(row);
        _categoryRows.Add(row);
        // 새 줄에서 바로 경로를 고를 수 있게 폴더 선택을 띄움
        var picked = PickFolder(null);
        if (picked != null)
        {
            row.Path = picked;
            if (string.IsNullOrWhiteSpace(row.Name)) row.Name = Path.GetFileName(picked.TrimEnd('\\'));
        }
        GridCategories.SelectedItem = row;
        GridCategories.ScrollIntoView(row);
    }

    private void ChkAllCategories_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = ((System.Windows.Controls.CheckBox)sender).IsChecked == true;
        foreach (var row in _categoryRows) row.Selected = isChecked;
    }

    private void BtnRemoveCategory_Click(object sender, RoutedEventArgs e)
    {
        var targets = _categoryRows.Where(r => r.Selected).ToList();
        // 체크된 분류가 없으면 그리드에서 선택(하이라이트)된 줄을 삭제
        if (targets.Count == 0 && GridCategories.SelectedItem is CategoryRow cur) targets.Add(cur);
        if (targets.Count == 0)
        {
            TxtStatusBar.Text = "삭제할 분류를 체크하거나 선택하세요.";
            return;
        }
        foreach (var row in targets) _categoryRows.Remove(row);
        RefreshCategoryUi();
        TxtStatusBar.Text = $"분류 {targets.Count}개를 목록에서 제거했습니다 — '설정 저장'을 눌러야 적용됩니다.";
    }

    private void BtnBrowseCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CategoryRow row) return;
        var picked = PickFolder(row.Path);
        if (picked == null) return;
        row.Path = picked;
        if (string.IsNullOrWhiteSpace(row.Name)) row.Name = Path.GetFileName(picked.TrimEnd('\\'));
    }

    private void BtnBrowseIllust_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder(TxtIllustRoot.Text.Trim());
        if (picked != null) TxtIllustRoot.Text = picked;
    }

    private static string? PickFolder(string? initial)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { ShowNewFolderButton = true };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dlg.SelectedPath = initial;
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    // ---------- 작가 목록 ----------

    /// <summary>모든 분류 루트의 폴더 + 수동 매핑 기준으로 그리드를 즉시 채운다 (네트워크 없이).</summary>
    private void LoadArtistsFromCache()
    {
        _suppressRowEvents = true;
        _artists.Clear();
        var roots = new List<(string Root, string Category)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var cat in _settings.CategoryRoots)
        {
            if (string.IsNullOrWhiteSpace(cat.Path)) continue;
            if (!Directory.Exists(cat.Path)) { missing.Add($"{cat.Name}: {cat.Path}"); continue; }
            if (!seenPaths.Add(Path.GetFullPath(cat.Path))) continue;
            roots.Add((cat.Path, cat.Name));
        }

        foreach (var (root, category) in roots)
        foreach (var dir in Directory.GetDirectories(root))
        {
            var folder = Path.GetFileName(dir);
            bool manual = _settings.Mapping.TryGetValue(folder, out var mapped);
            if (!manual && _settings.AutoMatches.TryGetValue(folder, out var auto)) mapped = auto;
            var row = new ArtistRow
            {
                FolderName = folder,
                Category = category,
                CreatorId = mapped ?? "",
                CreatorName = mapped != null && _settings.CreatorNames.TryGetValue(mapped, out var cachedName)
                    ? cachedName : "",
                Status = string.IsNullOrEmpty(mapped) ? "미확인" : (manual ? "수동 매핑" : "자동 매칭"),
                DownloadedCount = string.IsNullOrEmpty(mapped) ? 0 : _state.CountFor(mapped),
                Enabled = string.IsNullOrEmpty(mapped) || !_settings.DisabledCreators.Contains(mapped),
                IllustThreshold = mapped != null && _settings.CreatorIllustThresholds.TryGetValue(mapped, out var th)
                    ? th.ToString() : "",
            };
            HookRow(row);
            _artists.Add(row);
        }
        _suppressRowEvents = false;
        TxtStatusBar.Text = $"폴더 {_artists.Count}개. '작가 목록 새로고침'을 누르면 자동 매칭을 실행합니다."
            + (missing.Count > 0 ? $"  (접근 불가 경로 — {string.Join(", ", missing)})" : "");
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
            _settings.CreatorCategories[row.CreatorId] = row.Category;
            _settings.Save(App.SettingsPath);
        }

        // 기존 폴더를 새 분류의 루트로 이동할지 묻기
        var newRoot = _settings.FindCategory(row.Category)?.Path;
        if (string.IsNullOrWhiteSpace(newRoot)) return;
        var newPath = Path.Combine(newRoot, row.FolderName);
        if (Directory.Exists(newPath)) return;
        // 폴더가 현재 놓여 있는 다른 분류 루트를 찾는다
        var oldPath = _settings.CategoryRoots
            .Where(c => !string.IsNullOrWhiteSpace(c.Path)
                        && !string.Equals(c.Path, newRoot, StringComparison.OrdinalIgnoreCase))
            .Select(c => Path.Combine(c.Path, row.FolderName))
            .FirstOrDefault(Directory.Exists);
        if (oldPath == null) return;

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
        => ApplyArtistFilter();

    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyArtistFilter();

    /// <summary>분류 필터 + 검색어를 함께 적용</summary>
    private void ApplyArtistFilter()
    {
        if (GridArtists == null || CmbFilter == null || TxtSearch == null) return;
        var cat = CmbFilter.SelectedItem as string ?? "전체";
        var q = TxtSearch.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_artists);
        if (cat == "전체" && q.Length == 0) { view.Filter = null; return; }
        view.Filter = o =>
        {
            var r = (ArtistRow)o;
            bool catOk = cat switch
            {
                "전체" => true,
                "매칭 안 됨" => string.IsNullOrEmpty(r.CreatorId),
                _ => r.Category == cat,
            };
            if (!catOk) return false;
            return q.Length == 0
                || r.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.CreatorName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.CreatorId.Contains(q, StringComparison.OrdinalIgnoreCase);
        };
        TxtStatusBar.Text = $"표시 {view.Cast<object>().Count()} / 전체 {_artists.Count}";
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GridArtists.SelectedItem is not ArtistRow row) return;
        var root = new SyncEngine(_settings, _state, SaveState).RootFor(row.Category);
        var path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, row.FolderName);
        if (path == null || !Directory.Exists(path))
        {
            TxtStatusBar.Text = $"폴더를 찾을 수 없습니다: {path ?? row.FolderName}";
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void MenuOpenFanbox_Click(object sender, RoutedEventArgs e)
    {
        if (GridArtists.SelectedItem is not ArtistRow row || string.IsNullOrEmpty(row.CreatorId))
        {
            TxtStatusBar.Text = "매칭된 작가가 아닙니다 — 먼저 매핑하세요.";
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            $"https://{row.CreatorId}.fanbox.cc") { UseShellExecute = true });
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
                if (_settings.CreatorNames.TryGetValue(id, out var knownName))
                    row.CreatorName = knownName;
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

    /// <summary>팔로우/후원 작가 목록을 가져와 캐시에 저장. 쿠키 미설정 등이면 null.</summary>
    private async Task<List<CreatorInfo>?> FetchCreatorsAsync()
    {
        var account = _settings.Accounts.FirstOrDefault(a => a.Provider == "fanbox" && a.Enabled);
        if (account == null || string.IsNullOrWhiteSpace(account.SessionId))
        {
            MessageBox.Show("설정 탭에서 FANBOXSESSID 쿠키를 먼저 입력하세요.", "Fanbox Updater");
            return null;
        }
        using var provider = ProviderRegistry.Create(account, _settings, App.WebViewDataDir)!;
        await provider.InitializeAsync(CancellationToken.None);
        _creatorCache = await provider.GetFollowedCreatorsAsync(CancellationToken.None);
        // 작가명 캐시 갱신 — 재시작 후에도 그리드에 작가명이 바로 보이게
        foreach (var c in _creatorCache)
            _settings.CreatorNames[c.CreatorId] = c.DisplayName;
        _settings.Save(App.SettingsPath);
        return _creatorCache;
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_syncRunning) return;
        BtnRefresh.IsEnabled = false;
        TxtStatus.Text = "작가 목록 가져오는 중...";
        try
        {
            var creators = await FetchCreatorsAsync();
            if (creators == null) { TxtStatus.Text = "대기 중"; return; }
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

    // ---------- 수동 매핑 ----------

    private async void BtnMapCreator_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArtistRow row) return;

        if (_creatorCache.Count == 0)
        {
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = "작가 목록 가져오는 중...";
            try
            {
                if (await FetchCreatorsAsync() == null) { TxtStatus.Text = "대기 중"; return; }
                TxtStatus.Text = $"팔로우/후원 작가 {_creatorCache.Count}명";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "작가 목록 조회 실패";
                AppendLog($"[오류] {ex.Message}");
                return;
            }
            finally { BtnRefresh.IsEnabled = true; }
        }
        ShowCreatorPicker(row);
    }

    private void ShowCreatorPicker(ArtistRow row)
    {
        var all = _creatorCache.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        string Disp(CreatorInfo c) => $"{c.DisplayName}  ({c.CreatorId})";
        var byDisp = all.ToDictionary(Disp);

        var search = new Wpf.Ui.Controls.TextBox { PlaceholderText = "작가명 또는 creatorId 검색" };
        var list = new System.Windows.Controls.ListBox { Height = 320, Margin = new Thickness(0, 8, 0, 8) };
        void ApplyFilter()
        {
            var q = search.Text.Trim();
            list.ItemsSource = all
                .Where(c => q.Length == 0
                    || c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || c.CreatorId.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(Disp).ToList();
        }
        search.TextChanged += (_, _) => ApplyFilter();
        ApplyFilter();
        // 현재 매핑된 작가를 미리 선택
        if (!string.IsNullOrEmpty(row.CreatorId))
        {
            var cur = all.FirstOrDefault(c => string.Equals(c.CreatorId, row.CreatorId, StringComparison.OrdinalIgnoreCase));
            if (cur != null) { list.SelectedItem = Disp(cur); list.ScrollIntoView(list.SelectedItem); }
        }

        var ok = new System.Windows.Controls.Button { Content = "매핑", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        var unmap = new System.Windows.Controls.Button { Content = "매핑 해제", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "취소", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(unmap); buttons.Children.Add(cancel);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"'{row.FolderName}' 폴더와 매핑할 작가를 선택하세요.",
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(search);
        panel.Children.Add(list);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = "작가 수동 매핑",
            Content = panel,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        bool? result = null;
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        unmap.Click += (_, _) => { result = false; dlg.Close(); };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) { result = true; dlg.Close(); } };
        dlg.ShowDialog();

        if (result == false)
        {
            _settings.Mapping.Remove(row.FolderName);
            _settings.Save(App.SettingsPath);
            _suppressRowEvents = true;
            row.CreatorId = "";
            row.CreatorName = "";
            row.Status = "미확인";
            row.DownloadedCount = 0;
            _suppressRowEvents = false;
            TxtStatusBar.Text = $"'{row.FolderName}' 매핑을 해제했습니다.";
            return;
        }
        if (result != true || list.SelectedItem is not string disp || !byDisp.TryGetValue(disp, out var creator)) return;

        _settings.Mapping[row.FolderName] = creator.CreatorId;
        _settings.Save(App.SettingsPath);
        _suppressRowEvents = true;
        row.CreatorId = creator.CreatorId;
        row.CreatorName = creator.DisplayName;
        row.Status = "수동 매핑";
        row.DownloadedCount = _state.CountFor(creator.CreatorId);
        row.Enabled = !_settings.DisabledCreators.Contains(creator.CreatorId);
        row.IllustThreshold = _settings.CreatorIllustThresholds.TryGetValue(creator.CreatorId, out var th)
            ? th.ToString() : "";
        _suppressRowEvents = false;
        TxtStatusBar.Text = $"'{row.FolderName}' ↔ {creator.DisplayName} ({creator.CreatorId}) 매핑 저장됨";
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

        // 작가명 캐시 갱신 (이름을 모르는 매핑은 creatorId가 그대로 들어오므로 캐시를 덮어쓰지 않음)
        foreach (var m in matches)
        {
            if (m.CreatorId == null || string.IsNullOrEmpty(m.CreatorName)) continue;
            if (m.CreatorName == m.CreatorId && _settings.CreatorNames.ContainsKey(m.CreatorId)) continue;
            _settings.CreatorNames[m.CreatorId] = m.CreatorName;
        }
        // 자동 매칭 결과 캐시 — 재시작 후에도 그리드에 매칭 상태가 바로 보이게 (새로고침마다 재계산)
        var newAuto = new Dictionary<string, string>();
        foreach (var m in matches)
            if (m.CreatorId != null && !_settings.Mapping.ContainsKey(m.FolderName))
                newAuto[m.FolderName] = m.CreatorId;
        _settings.AutoMatches = newAuto;
        _settings.Save(App.SettingsPath);
    }

    // ---------- 동기화 ----------

    private void BtnSync_Click(object sender, RoutedEventArgs e) => StartSync();

    private void BtnRedownload_Click(object sender, RoutedEventArgs e)
    {
        if (_syncRunning) return;
        if (GridArtists.SelectedItem is not ArtistRow row || string.IsNullOrEmpty(row.CreatorId))
        {
            MessageBox.Show("작가 탭에서 다시 받을 작가를 먼저 선택하세요.", "Fanbox Updater");
            return;
        }

        // 날짜 선택 다이얼로그
        var picker = new System.Windows.Controls.DatePicker
        {
            SelectedDate = DateTime.Today.AddMonths(-3),
            Margin = new Thickness(0, 10, 0, 16),
        };
        var ok = new System.Windows.Controls.Button { Content = "다시 받기", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "취소", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"'{row.FolderName}' ({row.CreatorId}) 작가의\n이 날짜 이후 게시물을 받은 기록과 무관하게 다시 받습니다.\n이미 있는 파일은 건너뜁니다.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(picker);
        panel.Children.Add(buttons);
        var dlg = new Window
        {
            Title = "다시 받기",
            Content = panel,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        ok.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        if (dlg.ShowDialog() != true || picker.SelectedDate == null) return;

        StartSync(
            new Dictionary<string, DateTime> { [row.CreatorId] = picker.SelectedDate.Value },
            new HashSet<string> { row.CreatorId });
    }

    private async void StartSync(Dictionary<string, DateTime>? reDownloadSince = null,
        HashSet<string>? onlyCreators = null)
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
        BtnRefresh.IsEnabled = false;
        BtnRedownload.IsEnabled = false;
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
            var summary = await engine.RunAsync(provider, _syncCts.Token, reDownloadSince, onlyCreators);

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
            BtnRefresh.IsEnabled = true;
            BtnRedownload.IsEnabled = true;
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
        BtnUpdate.Content = "다운로드 중... 0%";
        Progress.Value = 0;
        TxtStatus.Text = "업데이트 다운로드 중...";
        try
        {
            _reallyExit = true;
            await _updater.DownloadAndApplyAsync(_pendingUpdate, p => Dispatcher.BeginInvoke(() =>
            {
                BtnUpdate.Content = $"다운로드 중... {p}%";
                Progress.Value = p;
                TxtStatus.Text = p >= 100 ? "업데이트 적용 중 — 곧 재시작합니다" : $"업데이트 다운로드 중... {p}%";
            })); // 재시작하며 적용
        }
        catch (Exception ex)
        {
            BtnUpdate.IsEnabled = true;
            BtnUpdate.Content = "업데이트";
            Progress.Value = 0;
            TxtStatus.Text = "대기 중";
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
