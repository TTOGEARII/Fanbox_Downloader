using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Updater.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;

namespace Updater.App;

/// <summary>설정 탭 — 위쪽은 사이트별 설정(현재 사이트 <see cref="_site"/>의 계정·저장 분류·게시물 필터·다운로드 옵션),
/// 아래쪽은 공통 설정(폴더 이름 규칙·스케줄·동작). 사이트를 바꾸면 사이트별 컨트롤만 그 사이트 값으로 다시 채운다.</summary>
public partial class MainWindow
{
    /// <summary>사이트별 컨트롤을 채우는 중 — 이때의 값 변경은 자동 반영하지 않는다</summary>
    private bool _loadingSiteUi;

    /// <summary>사이트마다 다른 안내 문구·주소</summary>
    private record SiteUiInfo(string CookieLabel, string CookieHint, string HomeUrl, string PostHint);

    private static SiteUiInfo SiteUi(string site) => site.ToLowerInvariant() switch
    {
        AppSettings.PatreonProvider => new SiteUiInfo(
            "session_id 쿠키",
            "브라우저에서 patreon.com 로그인 → F12 → Application → Cookies → patreon.com → session_id 값 복사. 후원(멤버십) 중인 크리에이터의 게시물을 받습니다. 작가 탭의 creatorId는 patreon:이름 형식입니다.",
            "https://www.patreon.com/home",
            "유료 = 후원자 전용 게시물, 무료 = 공개 게시물. 게시물에 실제로 첨부된 이미지·영상·파일만 받고, 미리보기(티저·썸네일) 이미지는 받지 않습니다."),
        _ => new SiteUiInfo(
            "FANBOXSESSID 쿠키",
            "브라우저에서 fanbox.cc 로그인 → F12 → Application → Cookies → fanbox.cc → FANBOXSESSID 값 복사. 만료되면('인증 실패') 다시 넣어주세요.",
            "https://www.fanbox.cc/",
            "유료 = 후원 플랜에 가입해서 볼 수 있는 게시물, 무료 = 누구나 볼 수 있는 공개 게시물."),
    };

    private void LoadSettingsToUi()
    {
        // 분류 그리드에는 모든 사이트의 분류가 들어 있고, 뷰 필터로 현재 사이트 것만 보인다
        _categoryRows.Clear();
        foreach (var (key, site) in _settings.Sites)
        foreach (var c in site.Categories)
        {
            var row = new CategoryRow { Provider = key.ToLowerInvariant(), Name = c.Name, Path = c.Path, SplitIllust = c.SplitIllust };
            HookCategoryRow(row);
            _categoryRows.Add(row);
        }
        CollectionViewSource.GetDefaultView(_categoryRows).Filter =
            o => ((CategoryRow)o).Provider.Equals(_site, StringComparison.OrdinalIgnoreCase);

        // 공통
        TxtTemplate.Text = _settings.FolderNameTemplate;
        TxtDateFormat.Text = _settings.DateFormat;
        TxtParallel.Text = _settings.MaxParallelDownloads.ToString();
        ChkFlat.IsChecked = _settings.FlatFiles;
        TxtFlatTemplate.Text = _settings.FlatFileNameTemplate;
        ChkFlatIllustOnly.IsChecked = _settings.FlatIllustOnly;
        ChkSchedule.IsChecked = _settings.Schedule.Enabled;
        RadDaily.IsChecked = _settings.Schedule.Mode == ScheduleSettings.ModeDaily;
        RadInterval.IsChecked = _settings.Schedule.Mode == ScheduleSettings.ModeInterval;
        TxtDailyTime.Text = _settings.Schedule.DailyTime;
        TxtIntervalHours.Text = _settings.Schedule.IntervalHours.ToString();
        ChkStartMin.IsChecked = _settings.StartMinimized;
        ChkCloseTray.IsChecked = _settings.CloseToTray;

        LoadSiteSettingsToUi();
        UpdateSetupBanner();
    }

    /// <summary>현재 사이트의 설정을 사이트별 컨트롤에 채운다 (사이트 전환 때마다)</summary>
    private void LoadSiteSettingsToUi()
    {
        _loadingSiteUi = true;
        try
        {
            var site = _settings.Site(_site);
            var label = CreatorIds.ProviderLabel(_site);
            var info = SiteUi(_site);

            TxtAccountTitle.Text = $"{label} 계정";
            LblCookie.Text = info.CookieLabel;
            PwdSession.Password = site.SessionId;
            RunCookieHint.Text = info.CookieHint;
            RunSiteLink.Text = $"{new Uri(info.HomeUrl).Host} 열기";

            TxtCategoryTitle.Text = $"{label} 저장 분류";
            TxtIllustRoot.Text = site.IllustRoot;
            TxtIllustMax.Text = site.IllustMaxImages.ToString();

            TxtDownloadTitle.Text = $"{label} 게시물 · 다운로드";
            ChkPaid.IsChecked = site.DownloadPaid;
            ChkFree.IsChecked = site.DownloadFree;
            TxtPostFilterHint.Text = info.PostHint;
            ChkSaveText.IsChecked = site.SaveText;
            ChkCover.IsChecked = site.DownloadCoverImage;
            ChkAutoCreate.IsChecked = site.AutoCreateFolders;
            ChkExternal.IsChecked = site.DownloadExternalLinks;
            ChkAutoExtract.IsChecked = site.AutoExtractArchives;
            ChkDeleteArchive.IsChecked = site.DeleteArchiveAfterExtract;
            ChkImages.IsChecked = site.FileTypes.Images;
            ChkVideos.IsChecked = site.FileTypes.Videos;
            ChkArchives.IsChecked = site.FileTypes.Archives;
            ChkOthers.IsChecked = site.FileTypes.Others;

            CollectionViewSource.GetDefaultView(_categoryRows).Refresh();
        }
        finally { _loadingSiteUi = false; }
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        // 편집 중인 셀 커밋 후 반영 (자동 반영 경로에서는 입력 중인 셀을 닫아버리지 않도록 버튼에서만)
        GridCategories.CommitEdit(DataGridEditingUnit.Row, true);
        ApplySettings(interactive: true);
    }

    /// <summary>설정 UI → _settings 반영 + 저장. 사이트별 컨트롤은 현재 사이트(<see cref="_site"/>)에 쓴다.
    /// 설정 탭은 자동 반영(디바운스)이라 버튼 없이도 호출된다 — interactive=false면 경고창 없이 조용히 넘어가고,
    /// 분류 루트가 바뀐 경우에만 작가 목록을 재스캔한다.</summary>
    private void ApplySettings(bool interactive)
    {
        if (_loadingSiteUi) return;

        // 분류 그리드 → 각 사이트의 Categories. 자동 반영 중에는 일시적으로 이름이 비거나 중복될 수 있어 조용히 거른다 (첫 번째 우선).
        // 분류 이름은 같은 사이트 안에서만 유일하면 된다
        var before = string.Join("\n", _settings.Sites.Select(kv =>
            kv.Key + ":" + string.Join("|", kv.Value.Categories.Select(c => $"{c.Name}={c.Path}"))));
        int dupCount = 0;
        foreach (var group in _categoryRows.GroupBy(r => r.Provider, StringComparer.OrdinalIgnoreCase))
        {
            var cats = group
                .Select(r => new CategoryRoot { Name = r.Name.Trim(), Path = r.Path.Trim(), SplitIllust = r.SplitIllust })
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dupCount += cats.RemoveAll(c => !seen.Add(c.Name));
            _settings.Site(group.Key).Categories = cats;
        }
        foreach (var site in _settings.Sites.Keys.Where(k => !_categoryRows.Any(r => r.Provider.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            _settings.Site(site).Categories = new();
        if (interactive && dupCount > 0) { ShowInfo("같은 사이트 안에 분류 이름이 중복되었습니다."); return; }
        var after = string.Join("\n", _settings.Sites.Select(kv =>
            kv.Key + ":" + string.Join("|", kv.Value.Categories.Select(c => $"{c.Name}={c.Path}"))));
        var rootsChanged = !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);

        // 사이트별
        var current = _settings.Site(_site);
        var newSession = PwdSession.Password.Trim();
        var sessionChanged = current.SessionId != newSession;
        current.SessionId = newSession;
        current.IllustRoot = TxtIllustRoot.Text.Trim();
        current.IllustMaxImages = ParseClamped(TxtIllustMax.Text, 1, 50, 4);
        current.DownloadPaid = ChkPaid.IsChecked == true;
        current.DownloadFree = ChkFree.IsChecked == true;
        current.SaveText = ChkSaveText.IsChecked == true;
        current.DownloadCoverImage = ChkCover.IsChecked == true;
        current.AutoCreateFolders = ChkAutoCreate.IsChecked == true;
        current.DownloadExternalLinks = ChkExternal.IsChecked == true;
        current.AutoExtractArchives = ChkAutoExtract.IsChecked == true;
        current.DeleteArchiveAfterExtract = ChkDeleteArchive.IsChecked == true;
        current.FileTypes.Images = ChkImages.IsChecked == true;
        current.FileTypes.Videos = ChkVideos.IsChecked == true;
        current.FileTypes.Archives = ChkArchives.IsChecked == true;
        current.FileTypes.Others = ChkOthers.IsChecked == true;

        // 공통
        _settings.FolderNameTemplate = string.IsNullOrWhiteSpace(TxtTemplate.Text) ? "{date} {title}" : TxtTemplate.Text;
        _settings.DateFormat = string.IsNullOrWhiteSpace(TxtDateFormat.Text) ? "yyyy-MM-dd" : TxtDateFormat.Text;
        _settings.MaxParallelDownloads = ParseClamped(TxtParallel.Text, 1, 8, 3);
        _settings.FlatFiles = ChkFlat.IsChecked == true;
        _settings.FlatFileNameTemplate = string.IsNullOrWhiteSpace(TxtFlatTemplate.Text) ? "{title}_{index}" : TxtFlatTemplate.Text.Trim();
        _settings.FlatIllustOnly = ChkFlatIllustOnly.IsChecked == true;
        _settings.Schedule.Enabled = ChkSchedule.IsChecked == true;
        _settings.Schedule.Mode = RadInterval.IsChecked == true ? ScheduleSettings.ModeInterval : ScheduleSettings.ModeDaily;
        _settings.Schedule.DailyTime = TimeOnly.TryParse(TxtDailyTime.Text, out _) ? TxtDailyTime.Text : "03:00";
        _settings.Schedule.IntervalHours = ParseClamped(TxtIntervalHours.Text, 1, 24 * 30, 6);
        _settings.StartMinimized = ChkStartMin.IsChecked == true;
        _settings.CloseToTray = ChkCloseTray.IsChecked == true;
        _settings.SelectedSite = _site;

        _settings.Save(App.SettingsPath);
        if (sessionChanged && !IsBusy) ReleaseProvider(_site); // 새 쿠키로 다시 로그인하게
        if (interactive || rootsChanged)
        {
            RefreshCategoryUi();
            LoadArtistsFromCache(); // 상태바에 '폴더 N개' 안내를 띄운다
        }
        UpdateNextRunText();
        UpdateSetupBanner();
        if (interactive) TxtStatusBar.Text = "설정을 저장했습니다.";
        else if (!rootsChanged) TxtStatusBar.Text = "설정이 자동 반영되었습니다.";
    }

    private static int ParseClamped(string text, int min, int max, int fallback)
        => int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;

    /// <summary>설정 컨트롤 변경 시 자동 반영을 예약한다 (600ms 디바운스).</summary>
    private void QueueAutoApply()
    {
        if (!_uiReady || _loadingSiteUi) return;
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    /// <summary>설정 탭의 모든 컨트롤 변경을 자동 반영에 연결한다.
    /// 텍스트박스는 타이핑마다 저장하지 않도록 포커스를 잃을 때 반영한다.</summary>
    private void WireAutoApply()
    {
        foreach (var toggle in new ToggleButton[]
                 {
                     ChkPaid, ChkFree, ChkFlat, ChkFlatIllustOnly, ChkSaveText, ChkCover, ChkAutoCreate, ChkExternal, ChkAutoExtract,
                     ChkDeleteArchive, ChkImages, ChkVideos, ChkArchives, ChkOthers, ChkSchedule,
                     ChkStartMin, ChkCloseTray, RadDaily, RadInterval,
                 })
        {
            toggle.Checked += (_, _) => QueueAutoApply();
            toggle.Unchecked += (_, _) => QueueAutoApply();
        }
        foreach (var tb in new Control[]
                 {
                     PwdSession, TxtIllustRoot, TxtIllustMax, TxtFlatTemplate, TxtTemplate, TxtDateFormat,
                     TxtParallel, TxtDailyTime, TxtIntervalHours,
                 })
            tb.LostFocus += (_, _) => QueueAutoApply();
    }

    /// <summary>처음 쓸 때 안내 배너 (현재 사이트 기준) — 쿠키나 저장 경로가 없으면 무엇을 해야 하는지 보여준다.</summary>
    private void UpdateSetupBanner()
    {
        string? title = null, msg = null;
        var label = CreatorIds.ProviderLabel(_site);
        var cookie = SiteUi(_site).CookieLabel;
        if (_settings.ActiveSite(_site) == null)
        {
            title = $"1단계 · {label} 쿠키를 입력하세요";
            msg = $"설정 탭 → {label} 계정에 브라우저의 {cookie} 값을 넣으면 {label} 팔로우/후원 작가 목록을 가져올 수 있습니다. (다른 사이트만 쓸 거면 위의 사이트 버튼으로 바꾸세요)";
        }
        else if (_settings.ExistingRoots(_site).Count == 0)
        {
            title = $"2단계 · {label} 저장 폴더를 지정하세요";
            msg = $"설정 탭 → {label} 저장 분류에서 게시물을 받을 폴더를 고르세요. 그 폴더 바로 아래 하위 폴더가 작가 폴더가 됩니다.";
        }
        else if (!SiteRows().Any() && !_settings.Site(_site).AutoCreateFolders)
        {
            title = $"3단계 · {label} 작가 폴더를 만드세요";
            msg = "저장 폴더 안에 작가별 폴더를 만들거나, 설정 탭의 '폴더 없는 팔로우 작가도 자동 생성'을 켜고 [작가 목록 새로고침]을 누르세요.";
        }
        SetupBanner.Visibility = title == null ? Visibility.Collapsed : Visibility.Visible;
        if (title != null) { TxtSetupTitle.Text = title; TxtSetupMsg.Text = msg; }
    }

    private void BtnGoSettings_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;

    private void LinkSite_Click(object sender, RoutedEventArgs e) => OpenExternal(SiteUi(_site).HomeUrl);

    private void BtnTestSession_Click(object sender, RoutedEventArgs e)
        => _ = TestSessionAsync(_site, BtnTestSession, SiteUi(_site).CookieLabel);

    /// <summary>쿠키로 실제 로그인되는지 확인 — 해당 사이트의 작가 목록 조회가 되면 성공.</summary>
    private async Task TestSessionAsync(string provider, System.Windows.Controls.Primitives.ButtonBase button, string cookieName)
    {
        _applyDebounce.Stop();
        ApplySettings(interactive: false);
        if (_settings.ActiveSite(provider) == null) { ShowInfo($"{cookieName}를 먼저 입력하세요."); return; }
        if (IsBusy) { ShowInfo("동기화가 끝난 뒤 확인하세요."); return; }

        var label = CreatorIds.ProviderLabel(provider);
        button.IsEnabled = false;
        TxtStatus.Text = $"{label} 로그인 확인 중...";
        try
        {
            var creators = await FetchCreatorsAsync(onlyProvider: provider);
            if (creators == null) return;
            ApplyMatchesForFetchedSites(creators);
            TxtStatus.Text = "대기 중";
            var count = creators.Count(c => string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase));
            ShowInfo($"{label} 로그인 성공 — 팔로우/후원 작가 {count}명을 확인했습니다.");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"{label} 로그인 확인 실패";
            AppendLog($"[오류] {ex.Message}");
            ShowInfo($"{label} 로그인에 실패했습니다.\n\n{ex.Message}");
        }
        finally { button.IsEnabled = true; }
    }

    // ---------- 분류 그리드 ----------

    /// <summary>사이트별 분류 콤보박스(작가 그리드·필터)를 분류 그리드 내용으로 갱신 — 분류 입력 즉시 반영.
    /// 작가 탭 필터 콤보는 현재 사이트의 분류만 보여준다.</summary>
    private void RefreshCategoryUi()
    {
        foreach (var site in ProviderRegistry.SupportedProviders)
        {
            var list = CategoriesFor(site);
            var names = _categoryRows
                .Where(r => r.Provider.Equals(site, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => r.Name).Distinct().ToList();
            if (list.SequenceEqual(names)) continue;
            list.Clear();
            foreach (var n in names) list.Add(n);
        }

        var selected = CmbFilter.SelectedItem as string ?? FilterAll;
        var items = new List<string> { FilterAll };
        items.AddRange(CategoriesFor(_site));
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

    /// <summary>분류 그리드에 보이는(현재 사이트의) 행</summary>
    private IEnumerable<CategoryRow> VisibleCategoryRows()
        => CollectionViewSource.GetDefaultView(_categoryRows).Cast<object>().OfType<CategoryRow>();

    private void BtnAddCategory_Click(object sender, RoutedEventArgs e)
    {
        var row = new CategoryRow { Provider = _site };
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
        QueueAutoApply();
    }

    private void ChkAllCategories_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = ((CheckBox)sender).IsChecked == true;
        foreach (var row in VisibleCategoryRows()) row.Selected = isChecked;
    }

    private void BtnRemoveCategory_Click(object sender, RoutedEventArgs e)
    {
        var targets = VisibleCategoryRows().Where(r => r.Selected).ToList();
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
