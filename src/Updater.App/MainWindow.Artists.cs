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

namespace Updater.App;

/// <summary>작가 탭 — 폴더 스캔, 매칭 결과 반영, 수동 매핑, 필터·검색, 컨텍스트 메뉴</summary>
public partial class MainWindow
{
    private const string FilterAll = "전체";
    private const string FilterUnmatched = "매칭 안 됨";

    private bool _suppressRowEvents;
    /// <summary>마지막으로 가져온 팔로우/후원 작가 목록 (수동 매핑 선택용)</summary>
    private List<CreatorInfo> _creatorCache = new();
    private bool _fetchingCreators;

    /// <summary>모든 분류 루트의 폴더 + 수동 매핑 기준으로 그리드를 즉시 채운다 (네트워크 없이).</summary>
    private void LoadArtistsFromCache()
    {
        _suppressRowEvents = true;
        _artists.Clear();
        var missing = new List<string>();
        foreach (var (root, category) in _settings.ExistingRoots(missing))
        foreach (var dir in Directory.GetDirectories(root))
        {
            var folder = Path.GetFileName(dir);
            bool manual = _settings.Mapping.TryGetValue(folder, out var mapped);
            if (!manual && _settings.AutoMatches.TryGetValue(folder, out var auto)) mapped = auto;
            var row = new ArtistRow { FolderName = folder, Category = category };
            FillRow(row, mapped, null, manual ? MatchStatus.Manual : MatchStatus.Auto);
            HookRow(row);
            _artists.Add(row);
        }
        _suppressRowEvents = false;
        RefreshAllSyncCheckState();
        UpdateSummary();
        UpdateSetupBanner();
        TxtStatusBar.Text = $"폴더 {_artists.Count}개. '작가 목록 새로고침'을 누르면 자동 매칭을 실행합니다."
            + (missing.Count > 0 ? $"  (접근 불가 경로 — {string.Join(", ", missing)})" : "");
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

    /// <summary>작가 탭 상단 요약: 폴더 수 · 매핑 · 미매핑 · 동기화 대상</summary>
    private void UpdateSummary()
    {
        int mapped = 0, enabled = 0;
        foreach (var r in _artists)
        {
            if (!r.IsMapped) continue;
            mapped++;
            if (r.Enabled) enabled++;
        }
        var unmapped = _artists.Count - mapped;
        TxtSummary.Text = $"폴더 {_artists.Count}개  ·  매핑 {mapped}  ·  미매핑 {unmapped}  ·  동기화 대상 {enabled}명";
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

    /// <summary>분류 필터 + 검색어를 함께 적용</summary>
    private void ApplyArtistFilter()
    {
        if (GridArtists == null || CmbFilter == null || TxtSearch == null) return;
        var cat = CmbFilter.SelectedItem as string ?? FilterAll;
        var q = TxtSearch.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_artists);
        if (cat == FilterAll && q.Length == 0)
        {
            view.Filter = null;
            RefreshAllSyncCheckState();
            return;
        }
        view.Filter = o =>
        {
            var r = (ArtistRow)o;
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
        TxtStatusBar.Text = $"표시 {VisibleRows().Count()} / 전체 {_artists.Count}";
        RefreshAllSyncCheckState();
    }

    // ---------- 컨텍스트 메뉴 · 더블클릭 ----------

    private ArtistRow? SelectedRow => GridArtists.SelectedItem as ArtistRow;

    private string? FolderPathOf(ArtistRow row)
    {
        var root = _settings.RootFor(row.Category);
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
                _settings.Mapping.Remove(row.FolderName);
                _settings.AutoMatches.Remove(row.FolderName); // 지운 매칭이 재시작 때 되살아나지 않게
                FillRow(row, null, null, MatchStatus.Manual);
            }
            else
            {
                var changed = !_settings.Mapping.TryGetValue(row.FolderName, out var prev) || prev != id;
                _settings.Mapping[row.FolderName] = id;
                if (changed) FillRow(row, id, null, MatchStatus.Manual);

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
    /// 한 사이트가 실패해도 나머지는 반영하며(로그에 기록), 전부 실패하면 예외.</summary>
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
        _creatorCache = _creatorCache.Where(c => !succeeded.Contains(c.Provider)).Concat(fetched).ToList();
        // 작가명 캐시 갱신 — 재시작 후에도 그리드에 작가명이 바로 보이게
        foreach (var c in fetched)
            _settings.CreatorNames[c.CreatorId] = c.DisplayName;
        _settings.Save(App.SettingsPath);
        return _creatorCache;
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
            ApplyMatches(new SyncEngine(_settings, _state, SaveState).MatchFolders(creators));
            TxtStatus.Text = $"작가 매칭 완료 — 팔로우/후원 {creators.Count}명";
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
        if (_creatorCache.Count == 0)
        {
            if (_syncRunning) { ShowInfo("동기화가 끝난 뒤 매핑하세요."); return; }
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
            finally { BtnRefresh.IsEnabled = !_syncRunning; }
        }

        var dlg = new CreatorPickerDialog(this, row.FolderName, _creatorCache, row.CreatorId);
        if (dlg.ShowDialog() != true) return;

        _suppressRowEvents = true;
        if (dlg.Unmapped)
        {
            _settings.Mapping.Remove(row.FolderName);
            _settings.AutoMatches.Remove(row.FolderName);
            FillRow(row, null, null, MatchStatus.Manual);
            TxtStatusBar.Text = $"'{row.FolderName}' 매핑을 해제했습니다.";
        }
        else if (dlg.Selected is { } creator)
        {
            _settings.Mapping[row.FolderName] = creator.CreatorId;
            FillRow(row, creator.CreatorId, creator.DisplayName, MatchStatus.Manual);
            TxtStatusBar.Text = $"'{row.FolderName}' ↔ {creator.DisplayName} ({creator.CreatorId}) 매핑 저장됨";
        }
        _suppressRowEvents = false;
        _settings.Save(App.SettingsPath);
        RefreshAllSyncCheckState();
        UpdateSummary();
    }

    private void ApplyMatches(List<FolderMatch> matches)
    {
        _suppressRowEvents = true;
        var byFolder = _artists.ToLookup(r => r.FolderName);
        foreach (var m in matches)
        {
            var row = byFolder[m.FolderName].FirstOrDefault(r => r.Category == m.Category)
                   ?? byFolder[m.FolderName].FirstOrDefault();
            if (row == null)
            {
                row = new ArtistRow { FolderName = m.FolderName, Category = m.Category };
                HookRow(row);
                _artists.Add(row);
            }
            row.Category = m.Category;
            if (m.CreatorId != null)
                FillRow(row, m.CreatorId, m.CreatorName,
                    _settings.Mapping.ContainsKey(m.FolderName) ? MatchStatus.Manual : MatchStatus.Auto);
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
        // 자동 매칭 결과 캐시 — 재시작 후에도 그리드에 매칭 상태가 바로 보이게 (새로고침마다 재계산)
        // 매칭이 하나도 없으면(작가 목록 조회 실패/쿠키 만료 등) 기존 캐시를 지우지 않고 보존한다
        if (matches.Any(m => m.CreatorId != null))
        {
            var newAuto = new Dictionary<string, string>();
            foreach (var m in matches)
                if (m.CreatorId != null && !_settings.Mapping.ContainsKey(m.FolderName))
                    newAuto[m.FolderName] = m.CreatorId;
            _settings.AutoMatches = newAuto;
        }
        _settings.Save(App.SettingsPath);
        RefreshAllSyncCheckState();
        UpdateSummary();
        UpdateSetupBanner();
    }
}
