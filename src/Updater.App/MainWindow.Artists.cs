using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Updater.Core;
using MessageBox = System.Windows.MessageBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Updater.App;

/// <summary>작가 탭 — 사이트 전환, 폴더 스캔, 매칭 결과 반영, 수동 매핑, 필터·검색, 컨텍스트 메뉴.
/// 작가·폴더·분류는 사이트별로 완전히 분리되어 있다: 그리드에는 현재 사이트(<see cref="_site"/>)의 행만 보이고,
/// 매칭·매핑·분류 콤보도 그 사이트 것만 쓴다.</summary>
public partial class MainWindow
{
    private const string FilterAll = "전체";
    private const string FilterUnmatched = "매칭 안 됨";

    private bool _suppressRowEvents;
    /// <summary>마지막으로 가져온 팔로우/후원 작가 목록 (모든 사이트, 수동 매핑 선택용)</summary>
    private List<CreatorInfo> _creatorCache = new();
    /// <summary>마지막 FetchCreatorsAsync에서 조회에 성공한 사이트 — 그 사이트의 폴더만 다시 매칭한다</summary>
    private HashSet<string> _lastFetchedSites = new(StringComparer.OrdinalIgnoreCase);
    private bool _fetchingCreators;
    /// <summary>사이트 전환 버튼들 (작가 탭·설정 탭 두 곳, 사이트당 하나씩)</summary>
    private readonly List<(string Site, RadioButton Button)> _siteButtons = new();
    private bool _switchingSite;

    // ---------- 사이트 전환 ----------

    /// <summary>작가 탭·설정 탭 상단의 사이트 전환 버튼을 지원 사이트 목록으로 만든다 (새 사이트가 추가되면 자동으로 늘어남).
    /// 두 탭의 버튼은 같은 <see cref="_site"/>를 가리키므로 한쪽을 바꾸면 다른 쪽도 따라간다.</summary>
    private void BuildSiteSwitcher()
    {
        _siteButtons.Clear();
        foreach (var (panel, group) in new[] { (SitePanel, "site"), (SettingsSitePanel, "settingsSite") })
        {
            panel.Children.Clear();
            foreach (var site in ProviderRegistry.SupportedProviders)
            {
                var btn = new RadioButton
                {
                    Content = CreatorIds.ProviderLabel(site),
                    GroupName = group,
                    Tag = site,
                    Style = (Style)FindResource("SiteToggle"),
                    IsChecked = string.Equals(site, _site, StringComparison.OrdinalIgnoreCase),
                };
                btn.Checked += (_, _) => { if (!_switchingSite) SelectSite(site); };
                _siteButtons.Add((site, btn));
                panel.Children.Add(btn);
            }
        }
    }

    /// <summary>현재 사이트를 바꾼다 — 작가 탭의 그리드 필터·분류 필터·요약·안내 배너와 설정 탭의 사이트별 설정이 모두 그 사이트 기준으로 바뀐다</summary>
    private void SelectSite(string site)
    {
        if (_switchingSite) return;
        _switchingSite = true;
        try
        {
            var changed = !string.Equals(_site, site, StringComparison.OrdinalIgnoreCase);
            if (changed && _uiReady)
            {
                // 설정 탭에 입력 중이던 이전 사이트의 값을 먼저 반영해 두고 바꾼다
                _applyDebounce.Stop();
                ApplySettings(interactive: false);
            }
            _site = site.ToLowerInvariant();
            _settings.SelectedSite = _site;
            foreach (var (s, btn) in _siteButtons)
                btn.IsChecked = string.Equals(s, _site, StringComparison.OrdinalIgnoreCase);
            if (CmbFilter == null) return;
            if (changed && _uiReady)
            {
                LoadSiteSettingsToUi();
                _settings.Save(App.SettingsPath);
            }
            RefreshCategoryUi();
            ApplyArtistFilter();
            UpdateSummary();
            UpdateSetupBanner();
        }
        finally { _switchingSite = false; }
    }

    /// <summary>사이트별 분류 콤보박스 컬렉션 (없으면 만든다)</summary>
    private ObservableCollection<string> CategoriesFor(string provider)
    {
        if (!_categoriesBySite.TryGetValue(provider, out var list))
            _categoriesBySite[provider] = list = new ObservableCollection<string>();
        return list;
    }

    /// <summary>현재 사이트의 행만</summary>
    private IEnumerable<ArtistRow> SiteRows() => _artists.Where(r => r.Provider.Equals(_site, StringComparison.OrdinalIgnoreCase));

    // ---------- 폴더 스캔 ----------

    /// <summary>모든 사이트의 분류 루트 폴더 + 수동 매핑 기준으로 그리드를 즉시 채운다 (네트워크 없이).</summary>
    private void LoadArtistsFromCache()
    {
        _suppressRowEvents = true;
        _artists.Clear();
        var missing = new List<string>();
        foreach (var (root, category, provider) in _settings.ExistingRoots(null, missing))
        foreach (var dir in Directory.GetDirectories(root))
        {
            var folder = Path.GetFileName(dir);
            var key = CreatorIds.FolderKey(provider, folder);
            bool manual = _settings.Mapping.TryGetValue(key, out var mapped);
            if (!manual && _settings.AutoMatches.TryGetValue(key, out var auto)) mapped = auto;
            var row = NewRow(provider, folder, category);
            FillRow(row, mapped, null, manual ? MatchStatus.Manual : MatchStatus.Auto);
            _artists.Add(row);
        }
        _suppressRowEvents = false;
        ApplyArtistFilter();
        RefreshAllSyncCheckState();
        UpdateSummary();
        UpdateSetupBanner();
        TxtStatusBar.Text = $"{CreatorIds.ProviderLabel(_site)} 폴더 {SiteRows().Count()}개. '작가 목록 새로고침'을 누르면 자동 매칭을 실행합니다."
            + (missing.Count > 0 ? $"  (접근 불가 경로 — {string.Join(", ", missing)})" : "");
    }

    private ArtistRow NewRow(string provider, string folder, string category)
    {
        var row = new ArtistRow { Provider = provider, FolderName = folder, Category = category, CategoryOptions = CategoriesFor(provider) };
        HookRow(row);
        return row;
    }

    /// <summary>행의 매핑 관련 칸을 설정·기록에서 채운다.
    /// creatorId가 비면 매핑 없음 — 상태는 '매칭 안 됨'이던 행만 유지하고 나머지는 '미확인'.</summary>
    private void FillRow(ArtistRow row, string? creatorId, string? creatorName, MatchStatus statusIfMapped)
    {
        var id = creatorId?.Trim() ?? "";
        row.CreatorId = id;
        if (id.Length == 0)
        {
            row.CreatorName = "";
            if (row.Status != MatchStatus.Unmatched) row.Status = MatchStatus.Unknown;
            row.DownloadedCount = 0;
            row.Enabled = false; // 미매핑 폴더는 동기화 대상이 아님
            row.IllustThreshold = "";
            return;
        }
        row.CreatorName = !string.IsNullOrEmpty(creatorName) && creatorName != id ? creatorName
                        : _settings.CreatorNames.TryGetValue(id, out var cached) ? cached
                        : creatorName ?? "";
        row.Status = statusIfMapped;
        row.DownloadedCount = _state.CountFor(id);
        row.Enabled = !_settings.DisabledCreators.Contains(id);
        row.IllustThreshold = _settings.CreatorIllustThresholds.TryGetValue(id, out var th) ? th.ToString() : "";
    }

    private void HookRow(ArtistRow row) => row.PropertyChanged += OnRowPropertyChanged;

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressRowEvents || sender is not ArtistRow row) return;
        switch (e.PropertyName)
        {
            case nameof(ArtistRow.Enabled): OnRowEnabledChanged(row); break;
            case nameof(ArtistRow.Category): OnRowCategoryChanged(row); break;
        }
    }

    private void OnRowEnabledChanged(ArtistRow row)
    {
        RefreshAllSyncCheckState();
        UpdateSummary();
        if (string.IsNullOrEmpty(row.CreatorId)) return;
        if (row.Enabled) _settings.DisabledCreators.Remove(row.CreatorId);
        else _settings.DisabledCreators.Add(row.CreatorId);
        _settings.Save(App.SettingsPath);
    }

    private IEnumerable<ArtistRow> VisibleRows()
        => CollectionViewSource.GetDefaultView(_artists).Cast<object>().OfType<ArtistRow>();

    /// <summary>헤더 전체선택 체크박스를 보이는 행들의 실제 상태에 맞춘다 (전체 켬 / 전체 끔 / 섞임).</summary>
    private void RefreshAllSyncCheckState()
    {
        if (ChkAllSync == null) return;
        int total = 0, on = 0;
        foreach (var r in VisibleRows()) { total++; if (r.Enabled) on++; }
        ChkAllSync.IsChecked = total == 0 ? false : on == total ? true : on == 0 ? false : null;
    }

    /// <summary>작가 탭 상단 요약 (현재 사이트): 폴더 수 · 매핑 · 미매핑 · 동기화 대상</summary>
    private void UpdateSummary()
    {
        int total = 0, mapped = 0, enabled = 0;
        foreach (var r in SiteRows())
        {
            total++;
            if (!r.IsMapped) continue;
            mapped++;
            if (r.Enabled) enabled++;
        }
        TxtSummary.Text = $"{CreatorIds.ProviderLabel(_site)}  ·  폴더 {total}개  ·  매핑 {mapped}  ·  미매핑 {total - mapped}  ·  동기화 대상 {enabled}명";
    }

    private void OnRowCategoryChanged(ArtistRow row)
    {
        if (!string.IsNullOrEmpty(row.CreatorId))
        {
            _settings.CreatorCategories[row.CreatorId] = row.Category;
            _settings.Save(App.SettingsPath);
        }

        // 기존 폴더를 새 분류의 루트로 이동할지 묻기 (같은 사이트의 분류끼리만)
        var newRoot = _settings.FindCategory(row.Provider, row.Category)?.Path;
        if (string.IsNullOrWhiteSpace(newRoot)) return;
        var newPath = Path.Combine(newRoot, row.FolderName);
        if (Directory.Exists(newPath)) return;
        // 폴더가 현재 놓여 있는 다른 분류 루트를 찾는다
        var oldPath = _settings.CategoryRootsFor(row.Provider)
            .Where(c => !string.IsNullOrWhiteSpace(c.Path)
                        && !string.Equals(c.Path, newRoot, StringComparison.OrdinalIgnoreCase))
            .Select(c => Path.Combine(c.Path, row.FolderName))
            .FirstOrDefault(Directory.Exists);
        if (oldPath == null) return;

        var answer = MessageBox.Show(
            $"'{row.FolderName}' 폴더를 {row.Category} 저장 경로로 이동할까요?\n\n{oldPath}\n→ {newPath}",
            "폴더 이동", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Directory.Move(oldPath, newPath);
            TxtStatusBar.Text = $"'{row.FolderName}' 폴더를 이동했습니다.";
        }
        catch (Exception ex)
        {
            ShowInfo($"폴더 이동 실패: {ex.Message}");
        }
    }

    private void ChkAllSync_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = ((CheckBox)sender).IsChecked == true;
        _suppressRowEvents = true;
        int count = 0;
        foreach (var row in VisibleRows().ToList())
        {
            row.Enabled = isChecked;
            count++;
            if (string.IsNullOrEmpty(row.CreatorId)) continue;
            if (isChecked) _settings.DisabledCreators.Remove(row.CreatorId);
            else _settings.DisabledCreators.Add(row.CreatorId);
        }
        _suppressRowEvents = false;
        _settings.Save(App.SettingsPath);
        RefreshAllSyncCheckState();
        UpdateSummary();
        TxtStatusBar.Text = $"{count}개 작가 동기화 {(isChecked ? "전체 선택" : "전체 해제")}";
    }

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyArtistFilter();

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyArtistFilter();

    /// <summary>현재 사이트 + 분류 필터 + 검색어를 함께 적용 (그리드에는 항상 현재 사이트의 행만 보인다)</summary>
    private void ApplyArtistFilter()
    {
        if (GridArtists == null || CmbFilter == null || TxtSearch == null) return;
        var cat = CmbFilter.SelectedItem as string ?? FilterAll;
        var q = TxtSearch.Text.Trim();
        var site = _site;
        var view = CollectionViewSource.GetDefaultView(_artists);
        view.Filter = o =>
        {
            var r = (ArtistRow)o;
            if (!r.Provider.Equals(site, StringComparison.OrdinalIgnoreCase)) return false;
            bool catOk = cat switch
            {
                FilterAll => true,
                FilterUnmatched => !r.IsMapped,
                _ => r.Category == cat,
            };
            if (!catOk) return false;
            return q.Length == 0
                || r.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.CreatorName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.CreatorId.Contains(q, StringComparison.OrdinalIgnoreCase);
        };
        if (cat != FilterAll || q.Length > 0)
            TxtStatusBar.Text = $"표시 {VisibleRows().Count()} / {CreatorIds.ProviderLabel(_site)} 전체 {SiteRows().Count()}";
        RefreshAllSyncCheckState();
    }

    // ---------- 컨텍스트 메뉴 · 더블클릭 ----------

    private ArtistRow? SelectedRow => GridArtists.SelectedItem as ArtistRow;

    private string? FolderPathOf(ArtistRow row)
    {
        var root = _settings.RootFor(row.Provider, row.Category);
        var path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, row.FolderName);
        return path != null && Directory.Exists(path) ? path : null;
    }

    private void GridArtists_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 셀 편집(더블클릭 진입)을 막지 않도록 편집 가능한 칸은 제외하고, 읽기 전용 칸에서만 폴더를 연다
        if (e.OriginalSource is DependencyObject d && FindCell(d) is { Column.IsReadOnly: true } && SelectedRow != null)
            OpenFolder(SelectedRow);
    }

    private static DataGridCell? FindCell(DependencyObject d)
    {
        for (var cur = d; cur != null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur is DataGridCell cell) return cell;
        return null;
    }

    private void OpenFolder(ArtistRow row)
    {
        var path = FolderPathOf(row);
        if (path == null)
        {
            TxtStatusBar.Text = $"폴더를 찾을 수 없습니다: {row.FolderName}";
            return;
        }
        OpenExternal(path);
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row) OpenFolder(row);
    }

    private void MenuOpenCreatorPage_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { IsMapped: true } row)
        {
            TxtStatusBar.Text = "매칭된 작가가 아닙니다 — 먼저 매핑하세요.";
            return;
        }
        OpenExternal(CreatorIds.CreatorPageUrl(row.CreatorId));
    }

    private void MenuMapCreator_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row) _ = MapCreatorAsync(row);
    }

    private void GridArtists_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        // creatorId / 일러 기준 칸 수동 입력 → 설정 저장 (바인딩 커밋 뒤에 읽도록 뒤로 미룬다)
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Row.Item is not ArtistRow row) return;
            var id = row.CreatorId.Trim();
            _suppressRowEvents = true;
            if (string.IsNullOrEmpty(id))
            {
                _settings.Mapping.Remove(row.FolderKey);
                _settings.AutoMatches.Remove(row.FolderKey); // 지운 매칭이 재시작 때 되살아나지 않게
                FillRow(row, null, null, MatchStatus.Manual);
            }
            else
            {
                // 사이트 접두어 없이 입력하면 이 행의 사이트로 보정하고, 다른 사이트 접두어면 거부
                if (id.IndexOf(':') < 0) id = CreatorIds.Qualify(row.Provider, id);
                if (!CreatorIds.ProviderOf(id).Equals(row.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    TxtStatusBar.Text = $"이 폴더는 {row.Site} 분류에 있어 {CreatorIds.SiteLabel(id)} 작가와 연결할 수 없습니다.";
                    FillRow(row, _settings.Mapping.GetValueOrDefault(row.FolderKey), null, MatchStatus.Manual);
                    _suppressRowEvents = false;
                    return;
                }
                var changed = !_settings.Mapping.TryGetValue(row.FolderKey, out var prev) || prev != id;
                _settings.Mapping[row.FolderKey] = id;
                if (changed || row.CreatorId != id) FillRow(row, id, null, MatchStatus.Manual);

                // 일러 기준 장수 (빈칸=전역 기본, 0=분리 안 함)
                var raw = row.IllustThreshold.Trim();
                if (string.IsNullOrEmpty(raw))
                    _settings.CreatorIllustThresholds.Remove(id);
                else if (int.TryParse(raw, out var th) && th >= 0)
                    _settings.CreatorIllustThresholds[id] = Math.Min(th, 100);
                else
                    row.IllustThreshold = ""; // 잘못된 입력은 초기화
            }
            _suppressRowEvents = false;
            _settings.Save(App.SettingsPath);
            RefreshAllSyncCheckState();
            UpdateSummary();
        }, DispatcherPriority.Background);
    }

    // ---------- 작가 목록 · 매칭 ----------

    /// <summary>로그인된 모든 사이트의 팔로우/후원 작가 목록을 가져와 캐시에 저장. 쿠키 미설정이면 안내 후 null.
    /// 한 사이트가 실패해도 나머지는 반영하며(로그에 기록), 전부 실패하면 예외. 성공한 사이트는 <see cref="_lastFetchedSites"/>에 남는다.</summary>
    /// <param name="onlyProvider">지정하면 그 사이트만 새로 가져오고 다른 사이트의 캐시는 유지 (연결 확인 버튼용)</param>
    private async Task<List<CreatorInfo>?> FetchCreatorsAsync(string? onlyProvider = null)
    {
        var providers = await AcquireProvidersAsync(CancellationToken.None, onlyProvider);
        if (providers == null) return null;
        _fetchingCreators = true;
        var fetched = new List<CreatorInfo>();
        var errors = new List<string>();
        var succeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var provider in providers)
            {
                try
                {
                    fetched.AddRange(await provider.GetFollowedCreatorsAsync(CancellationToken.None));
                    succeeded.Add(provider.Name);
                }
                catch (Exception ex)
                {
                    errors.Add($"{CreatorIds.ProviderLabel(provider.Name)}: {ex.Message}");
                    ReleaseProvider(provider.Name); // 세션 만료 등은 다음에 새로 띄워서 복구
                }
            }
        }
        finally { _fetchingCreators = false; }
        foreach (var e in errors) AppendLog($"[오류] 작가 목록 조회 실패 — {e}");
        if (succeeded.Count == 0)
            throw new InvalidOperationException(string.Join("\n", errors));

        // 조회에 성공한 사이트의 작가만 교체하고, 이번에 조회하지 않았거나 실패한 사이트의 캐시는 유지
        _lastFetchedSites = succeeded;
        _creatorCache = _creatorCache.Where(c => !succeeded.Contains(c.Provider)).Concat(fetched).ToList();
        // 작가명 캐시 갱신 — 재시작 후에도 그리드에 작가명이 바로 보이게
        foreach (var c in fetched)
            _settings.CreatorNames[c.CreatorId] = c.DisplayName;
        _settings.Save(App.SettingsPath);
        return _creatorCache;
    }

    /// <summary>마지막 조회에 성공한 사이트들의 폴더를 다시 매칭해 그리드에 반영</summary>
    private void ApplyMatchesForFetchedSites(List<CreatorInfo> creators)
    {
        var engine = new SyncEngine(_settings, _state, SaveState);
        foreach (var site in _lastFetchedSites)
            ApplyMatches(engine.MatchFolders(creators, site));
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        BtnRefresh.IsEnabled = false;
        TxtStatus.Text = "작가 목록 가져오는 중...";
        try
        {
            var creators = await FetchCreatorsAsync();
            if (creators == null) { TxtStatus.Text = "대기 중"; return; }
            ApplyMatchesForFetchedSites(creators);
            TxtStatus.Text = "작가 매칭 완료 — " + string.Join(", ", _lastFetchedSites.Select(s =>
                $"{CreatorIds.ProviderLabel(s)} {creators.Count(c => c.Provider.Equals(s, StringComparison.OrdinalIgnoreCase))}명"));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "작가 목록 조회 실패";
            AppendLog($"[오류] {ex.Message}");
        }
        finally { BtnRefresh.IsEnabled = !_syncRunning; }
    }

    private void BtnMapCreator_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ArtistRow row) _ = MapCreatorAsync(row);
    }

    private async Task MapCreatorAsync(ArtistRow row)
    {
        // 이 폴더의 사이트 작가 목록이 없으면 그 사이트만 가져온다
        if (!_creatorCache.Any(c => c.Provider.Equals(row.Provider, StringComparison.OrdinalIgnoreCase)))
        {
            if (_syncRunning) { ShowInfo("동기화가 끝난 뒤 매핑하세요."); return; }
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = $"{row.Site} 작가 목록 가져오는 중...";
            try
            {
                if (await FetchCreatorsAsync(onlyProvider: row.Provider) == null) { TxtStatus.Text = "대기 중"; return; }
                TxtStatus.Text = $"{row.Site} 팔로우/후원 작가 {_creatorCache.Count(c => c.Provider.Equals(row.Provider, StringComparison.OrdinalIgnoreCase))}명";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "작가 목록 조회 실패";
                AppendLog($"[오류] {ex.Message}");
                return;
            }
            finally { BtnRefresh.IsEnabled = !_syncRunning; }
        }

        var candidates = _creatorCache.Where(c => c.Provider.Equals(row.Provider, StringComparison.OrdinalIgnoreCase)).ToList();
        var dlg = new CreatorPickerDialog(this, row.FolderName, candidates, row.CreatorId);
        if (dlg.ShowDialog() != true) return;

        _suppressRowEvents = true;
        if (dlg.Unmapped)
        {
            _settings.Mapping.Remove(row.FolderKey);
            _settings.AutoMatches.Remove(row.FolderKey);
            FillRow(row, null, null, MatchStatus.Manual);
            TxtStatusBar.Text = $"'{row.FolderName}' 매핑을 해제했습니다.";
        }
        else if (dlg.Selected is { } creator)
        {
            _settings.Mapping[row.FolderKey] = creator.CreatorId;
            FillRow(row, creator.CreatorId, creator.DisplayName, MatchStatus.Manual);
            TxtStatusBar.Text = $"'{row.FolderName}' ↔ {creator.DisplayName} ({creator.CreatorId}) 매핑 저장됨";
        }
        _suppressRowEvents = false;
        _settings.Save(App.SettingsPath);
        RefreshAllSyncCheckState();
        UpdateSummary();
    }

    /// <summary>매칭 결과를 그리드에 반영. matches에 들어 있는 사이트의 행만 건드린다 (다른 사이트는 그대로).</summary>
    private void ApplyMatches(List<FolderMatch> matches)
    {
        _suppressRowEvents = true;
        var byKey = _artists.ToLookup(r => r.FolderKey, StringComparer.OrdinalIgnoreCase);
        foreach (var m in matches)
        {
            var key = CreatorIds.FolderKey(m.Provider, m.FolderName);
            var row = byKey[key].FirstOrDefault(r => r.Category == m.Category)
                   ?? byKey[key].FirstOrDefault();
            if (row == null)
            {
                row = NewRow(m.Provider, m.FolderName, m.Category);
                _artists.Add(row);
            }
            row.Category = m.Category;
            if (m.CreatorId != null)
                FillRow(row, m.CreatorId, m.CreatorName,
                    _settings.Mapping.ContainsKey(key) ? MatchStatus.Manual : MatchStatus.Auto);
            else
            {
                row.Status = MatchStatus.Unmatched;
                FillRow(row, null, null, MatchStatus.Auto);
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
        // 자동 매칭 결과 캐시 — 재시작 후에도 그리드에 매칭 상태가 바로 보이게. matches에 있는 사이트의 항목만 새로 쓴다.
        // 매칭이 하나도 없으면(작가 목록 조회 실패/쿠키 만료 등) 기존 캐시를 지우지 않고 보존한다
        if (matches.Any(m => m.CreatorId != null))
        {
            var sites = new HashSet<string>(matches.Select(m => m.Provider), StringComparer.OrdinalIgnoreCase);
            foreach (var k in _settings.AutoMatches.Keys.Where(k => sites.Contains(CreatorIds.ProviderOf(k))).ToList())
                _settings.AutoMatches.Remove(k);
            foreach (var m in matches)
            {
                var key = CreatorIds.FolderKey(m.Provider, m.FolderName);
                if (m.CreatorId != null && !_settings.Mapping.ContainsKey(key))
                    _settings.AutoMatches[key] = m.CreatorId;
            }
        }
        _settings.Save(App.SettingsPath);
        ApplyArtistFilter();
        RefreshAllSyncCheckState();
        UpdateSummary();
        UpdateSetupBanner();
    }
}
