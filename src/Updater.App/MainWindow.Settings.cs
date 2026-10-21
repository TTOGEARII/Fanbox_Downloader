using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Updater.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;

namespace Updater.App;

/// <summary>설정 탭 — UI ↔ AppSettings 반영, 자동 저장, 분류 그리드, 제거</summary>
public partial class MainWindow
{
    private void LoadSettingsToUi()
    {
        PwdSession.Password = _settings.GetAccount(AppSettings.FanboxProvider)?.SessionId ?? "";
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
        ChkAutoExtract.IsChecked = _settings.AutoExtractArchives;
        ChkDeleteArchive.IsChecked = _settings.DeleteArchiveAfterExtract;
        ChkImages.IsChecked = _settings.FileTypes.Images;
        ChkVideos.IsChecked = _settings.FileTypes.Videos;
        ChkArchives.IsChecked = _settings.FileTypes.Archives;
        ChkOthers.IsChecked = _settings.FileTypes.Others;
        ChkSchedule.IsChecked = _settings.Schedule.Enabled;
        RadDaily.IsChecked = _settings.Schedule.Mode == ScheduleSettings.ModeDaily;
        RadInterval.IsChecked = _settings.Schedule.Mode == ScheduleSettings.ModeInterval;
        TxtDailyTime.Text = _settings.Schedule.DailyTime;
        TxtIntervalHours.Text = _settings.Schedule.IntervalHours.ToString();
        ChkStartMin.IsChecked = _settings.StartMinimized;
        ChkCloseTray.IsChecked = _settings.CloseToTray;
        UpdateSetupBanner();
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        // 편집 중인 셀 커밋 후 반영 (자동 반영 경로에서는 입력 중인 셀을 닫아버리지 않도록 버튼에서만)
        GridCategories.CommitEdit(DataGridEditingUnit.Row, true);
        ApplySettings(interactive: true);
    }

    /// <summary>설정 UI → _settings 반영 + 저장. 설정 탭은 자동 반영(디바운스)이라 버튼 없이도 호출된다 —
    /// interactive=false면 경고창 없이 조용히 넘어가고, 분류 루트가 바뀐 경우에만 작가 목록을 재스캔한다.</summary>
    private void ApplySettings(bool interactive)
    {
        // 분류 그리드 → 설정. 자동 반영 중에는 일시적으로 이름이 비거나 중복될 수 있어 조용히 거른다 (첫 번째 우선)
        var cats = _categoryRows
            .Select(r => new CategoryRoot { Name = r.Name.Trim(), Path = r.Path.Trim(), SplitIllust = r.SplitIllust })
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .ToList();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dupCount = cats.RemoveAll(c => !seenNames.Add(c.Name));
        if (interactive)
        {
            if (cats.Count == 0) { ShowInfo("저장 분류를 최소 1개 입력하세요."); return; }
            if (dupCount > 0) { ShowInfo("분류 이름이 중복되었습니다."); return; }
        }

        var fanbox = _settings.EnsureAccount(AppSettings.FanboxProvider);
        var newSession = PwdSession.Password.Trim();
        var sessionChanged = fanbox.SessionId != newSession;
        fanbox.SessionId = newSession;

        var rootsChanged = false;
        if (cats.Count > 0) // 자동 반영 중 전부 지워진 순간은 기존 분류를 유지 (최소 1개 필요)
        {
            rootsChanged = !cats.Select(c => $"{c.Name}|{c.Path}").SequenceEqual(
                _settings.CategoryRoots.Select(c => $"{c.Name}|{c.Path}"), StringComparer.OrdinalIgnoreCase);
            _settings.CategoryRoots = cats;
        }

        _settings.IllustRoot = TxtIllustRoot.Text.Trim();
        _settings.IllustMaxImages = ParseClamped(TxtIllustMax.Text, 1, 50, 4);
        _settings.FolderNameTemplate = string.IsNullOrWhiteSpace(TxtTemplate.Text) ? "{date} {title}" : TxtTemplate.Text;
        _settings.DateFormat = string.IsNullOrWhiteSpace(TxtDateFormat.Text) ? "yyyy-MM-dd" : TxtDateFormat.Text;
        _settings.MaxParallelDownloads = ParseClamped(TxtParallel.Text, 1, 8, 3);
        _settings.SaveText = ChkSaveText.IsChecked == true;
        _settings.DownloadCoverImage = ChkCover.IsChecked == true;
        _settings.AutoCreateFolders = ChkAutoCreate.IsChecked == true;
        _settings.PaidPostsOnly = ChkPaidOnly.IsChecked == true;
        _settings.DownloadExternalLinks = ChkExternal.IsChecked == true;
        _settings.AutoExtractArchives = ChkAutoExtract.IsChecked == true;
        _settings.DeleteArchiveAfterExtract = ChkDeleteArchive.IsChecked == true;
        _settings.FileTypes.Images = ChkImages.IsChecked == true;
        _settings.FileTypes.Videos = ChkVideos.IsChecked == true;
        _settings.FileTypes.Archives = ChkArchives.IsChecked == true;
        _settings.FileTypes.Others = ChkOthers.IsChecked == true;
        _settings.Schedule.Enabled = ChkSchedule.IsChecked == true;
        _settings.Schedule.Mode = RadInterval.IsChecked == true ? ScheduleSettings.ModeInterval : ScheduleSettings.ModeDaily;
        _settings.Schedule.DailyTime = TimeOnly.TryParse(TxtDailyTime.Text, out _) ? TxtDailyTime.Text : "03:00";
        _settings.Schedule.IntervalHours = ParseClamped(TxtIntervalHours.Text, 1, 24 * 30, 6);
        _settings.StartMinimized = ChkStartMin.IsChecked == true;
        _settings.CloseToTray = ChkCloseTray.IsChecked == true;

        _settings.Save(App.SettingsPath);
        if (sessionChanged && !IsBusy) ReleaseProvider(); // 새 쿠키로 다시 로그인하게
        if (interactive || rootsChanged)
        {
            RefreshCategoryUi();
            LoadArtistsFromCache(); // 상태바에 '폴더 N개' 안내를 띄운다
        }
        UpdateNextRunText();
        UpdateSetupBanner();
        if (interactive) TxtStatusBar.Text = "설정을 저장했습니다.";
        else if (cats.Count == 0) TxtStatusBar.Text = "분류가 모두 비어 있어 기존 저장 분류를 유지합니다 (최소 1개 필요).";
        else if (!rootsChanged) TxtStatusBar.Text = "설정이 자동 반영되었습니다.";
    }

    private static int ParseClamped(string text, int min, int max, int fallback)
        => int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;

    /// <summary>설정 컨트롤 변경 시 자동 반영을 예약한다 (600ms 디바운스).</summary>
    private void QueueAutoApply()
    {
        if (!_uiReady) return;
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    /// <summary>설정 탭의 모든 컨트롤 변경을 자동 반영에 연결한다.
    /// 텍스트박스는 타이핑마다 저장하지 않도록 포커스를 잃을 때 반영한다.</summary>
    private void WireAutoApply()
    {
        foreach (var toggle in new ToggleButton[]
                 {
                     ChkSaveText, ChkCover, ChkAutoCreate, ChkPaidOnly, ChkExternal, ChkAutoExtract,
                     ChkDeleteArchive, ChkImages, ChkVideos, ChkArchives, ChkOthers, ChkSchedule,
                     ChkStartMin, ChkCloseTray, RadDaily, RadInterval,
                 })
        {
            toggle.Checked += (_, _) => QueueAutoApply();
            toggle.Unchecked += (_, _) => QueueAutoApply();
        }
        foreach (var tb in new Control[]
                 {
                     PwdSession, TxtIllustRoot, TxtIllustMax, TxtTemplate, TxtDateFormat,
                     TxtParallel, TxtDailyTime, TxtIntervalHours,
                 })
            tb.LostFocus += (_, _) => QueueAutoApply();
    }

    /// <summary>처음 쓸 때 안내 배너 — 쿠키나 저장 경로가 없으면 무엇을 해야 하는지 보여준다.</summary>
    private void UpdateSetupBanner()
    {
        string? title = null, msg = null;
        if (_settings.ActiveFanboxAccount == null)
        {
            title = "1단계 · FANBOX 쿠키를 입력하세요";
            msg = "설정 탭 → 계정에 브라우저의 FANBOXSESSID 쿠키 값을 넣으면 팔로우/후원 작가 목록을 가져올 수 있습니다.";
        }
        else if (_settings.ExistingRoots().Count == 0)
        {
            title = "2단계 · 저장 폴더를 지정하세요";
            msg = "설정 탭 → 저장 분류에서 게시물을 받을 폴더를 고르세요. 그 폴더 바로 아래 하위 폴더가 작가 폴더가 됩니다.";
        }
        else if (_artists.Count == 0 && !_settings.AutoCreateFolders)
        {
            title = "3단계 · 작가 폴더를 만드세요";
            msg = "저장 폴더 안에 작가별 폴더를 만들거나, 설정 탭의 '폴더 없는 팔로우 작가도 자동 생성'을 켜고 [작가 목록 새로고침]을 누르세요.";
        }
        SetupBanner.Visibility = title == null ? Visibility.Collapsed : Visibility.Visible;
        if (title != null) { TxtSetupTitle.Text = title; TxtSetupMsg.Text = msg; }
    }

    private void BtnGoSettings_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;

    private void LinkFanbox_Click(object sender, RoutedEventArgs e) => OpenExternal("https://www.fanbox.cc/");

    /// <summary>쿠키로 실제 로그인되는지 확인 — 작가 목록 조회가 되면 성공.</summary>
    private async void BtnTestSession_Click(object sender, RoutedEventArgs e)
    {
        _applyDebounce.Stop();
        ApplySettings(interactive: false);
        if (_settings.ActiveFanboxAccount == null) { ShowInfo("FANBOXSESSID 쿠키를 먼저 입력하세요."); return; }
        if (IsBusy) { ShowInfo("동기화가 끝난 뒤 확인하세요."); return; }

        BtnTestSession.IsEnabled = false;
        TxtStatus.Text = "로그인 확인 중...";
        try
        {
            var creators = await FetchCreatorsAsync();
            if (creators == null) return;
            ApplyMatches(new SyncEngine(_settings, _state, SaveState).MatchFolders(creators));
            TxtStatus.Text = "대기 중";
            ShowInfo($"로그인 성공 — 팔로우/후원 작가 {creators.Count}명을 확인했습니다.");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "로그인 확인 실패";
            AppendLog($"[오류] {ex.Message}");
            ShowInfo($"로그인에 실패했습니다.\n\n{ex.Message}");
        }
        finally { BtnTestSession.IsEnabled = true; }
    }

    // ---------- 분류 그리드 ----------

    /// <summary>분류 콤보박스(작가 그리드·필터)를 분류 그리드 내용으로 갱신 — 분류 입력 즉시 반영</summary>
    private void RefreshCategoryUi()
    {
        Categories.Clear();
        foreach (var r in _categoryRows.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
            if (!Categories.Contains(r.Name)) Categories.Add(r.Name);

        var selected = CmbFilter.SelectedItem as string ?? FilterAll;
        var items = new List<string> { FilterAll };
        items.AddRange(Categories);
        items.Add(FilterUnmatched);
        CmbFilter.ItemsSource = items;
        CmbFilter.SelectedItem = items.Contains(selected) ? selected : FilterAll;
    }

    private void HookCategoryRow(CategoryRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CategoryRow.Name)) RefreshCategoryUi();
            if (e.PropertyName is nameof(CategoryRow.Name) or nameof(CategoryRow.Path) or nameof(CategoryRow.SplitIllust))
                QueueAutoApply();
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
        var isChecked = ((CheckBox)sender).IsChecked == true;
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
        QueueAutoApply();
        TxtStatusBar.Text = $"분류 {targets.Count}개를 제거했습니다.";
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
        if (picked == null) return;
        TxtIllustRoot.Text = picked;
        QueueAutoApply();
    }

    private static string? PickFolder(string? initial)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { ShowNewFolderButton = true };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dlg.SelectedPath = initial;
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    // ---------- 제거 ----------

    private void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (!_updater.IsInstalled)
        {
            ShowInfo("개발 실행에서는 제거할 수 없습니다. Setup.exe로 설치된 경우에만 동작합니다.");
            return;
        }
        if (MessageBox.Show(
                "Fanbox Updater를 제거할까요?\n\n받아둔 파일과 저장 폴더는 지우지 않습니다.",
                "프로그램 제거", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var wipeData = MessageBox.Show(
            $"설정과 받은 기록도 함께 삭제할까요?\n({App.DataDir})\n\n[예] 흔적 없이 삭제 — 재설치하면 처음부터 다시 설정하고, 게시물도 전부 다시 받습니다.\n[아니요] 남겨두기 — 재설치하면 그대로 이어서 씁니다.",
            "설정·기록 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        try
        {
            UpdateService.LaunchUninstaller();
        }
        catch (Exception ex)
        {
            ShowInfo($"제거 실행에 실패했습니다: {ex.Message}\n\nWindows 설정 → 앱 → 설치된 앱에서 제거해주세요.");
            return;
        }

        _uninstalling = true; // 종료 시 자동 저장이 설정 폴더를 되살리지 않게
        ReleaseProvider();
        if (wipeData)
        {
            try { Directory.Delete(App.DataDir, recursive: true); }
            catch { /* 열려 있는 로그 등은 무시 — 언인스톨러가 지울 수 있는 건 지운다 */ }
        }
        _reallyExit = true;
        _tray?.Dispose();
        Application.Current.Shutdown();
    }
}
