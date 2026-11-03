using System.Windows;
using Updater.Core;

namespace Updater.App;

/// <summary>동기화 실행 · 프로바이더(WebView2) 재사용 · 스케줄러</summary>
public partial class MainWindow
{
    /// <summary>프로바이더를 마지막 사용 뒤 이만큼 쓰지 않으면 해제한다 (숨긴 WebView2가 메모리를 계속 차지하지 않게)</summary>
    private static readonly TimeSpan ProviderIdleTimeout = TimeSpan.FromMinutes(5);

    private CancellationTokenSource? _syncCts;
    private bool _syncRunning;
    /// <summary>프로바이더 이름 → 띄워 둔 프로바이더와 그때의 세션 쿠키 (사이트마다 하나)</summary>
    private readonly Dictionary<string, (ISiteProvider Provider, string Session)> _providers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _providerLastUsed;

    /// <summary>네트워크 작업 중(동기화 또는 작가 목록 조회) — 이때는 프로바이더를 바꾸거나 해제하지 않는다</summary>
    private bool IsBusy => _syncRunning || _fetchingCreators;

    /// <summary>로그인된 프로바이더들을 돌려준다. 한 번 띄운 WebView2를 쿠키가 바뀌거나 유휴 시간이 지날 때까지 재사용하므로
    /// '작가 목록 새로고침' 뒤 바로 '지금 최신화'를 눌러도 브라우저 엔진을 다시 띄우지 않는다.
    /// 쿠키가 하나도 없으면 안내 후 null. 일부 사이트 로그인이 실패하면 로그에 남기고 나머지로 진행하며, 전부 실패하면 예외.</summary>
    /// <param name="onlyProvider">지정하면 그 사이트만 (연결 확인 버튼용)</param>
    private async Task<List<ISiteProvider>?> AcquireProvidersAsync(CancellationToken ct, string? onlyProvider = null)
    {
        var accounts = _settings.ActiveSites;
        if (onlyProvider != null)
            accounts = accounts.Where(a => string.Equals(a.Provider, onlyProvider, StringComparison.OrdinalIgnoreCase)).ToList();
        if (accounts.Count == 0)
        {
            ShowInfo(onlyProvider == AppSettings.PatreonProvider
                ? "설정 탭에서 Patreon session_id 쿠키를 먼저 입력하세요."
                : "설정 탭에서 FANBOXSESSID 또는 Patreon session_id 쿠키를 먼저 입력하세요.");
            Tabs.SelectedIndex = 1;
            if (onlyProvider != null) SelectSite(onlyProvider);
            PwdSession.Focus();
            return null;
        }

        var result = new List<ISiteProvider>();
        var errors = new List<string>();
        foreach (var account in accounts)
        {
            var label = CreatorIds.ProviderLabel(account.Provider);
            if (_providers.TryGetValue(account.Provider, out var cur) && cur.Session != account.SessionId && !IsBusy)
                ReleaseProvider(account.Provider);
            if (!_providers.ContainsKey(account.Provider))
            {
                var prev = TxtStatus.Text;
                TxtStatus.Text = $"브라우저 엔진 초기화 중... ({label})";
                var p = ProviderRegistry.Create(account, _settings, App.WebViewDataDir)!;
                try { await p.InitializeAsync(ct); }
                catch (OperationCanceledException) { p.Dispose(); TxtStatus.Text = prev; throw; }
                catch (Exception ex)
                {
                    p.Dispose();
                    TxtStatus.Text = prev;
                    errors.Add($"{label}: {ex.Message}");
                    continue;
                }
                _providers[account.Provider] = (p, account.SessionId);
                TxtStatus.Text = prev;
            }
            result.Add(_providers[account.Provider].Provider);
        }
        foreach (var e in errors) AppendLog($"[오류] 로그인 실패 — {e}");
        if (result.Count == 0)
            throw new InvalidOperationException(string.Join("\n", errors));
        _providerLastUsed = DateTime.Now;
        return result;
    }

    /// <summary>띄워 둔 프로바이더 전부 해제</summary>
    private void ReleaseProvider()
    {
        foreach (var name in _providers.Keys.ToList()) ReleaseProvider(name);
    }

    private void ReleaseProvider(string providerName)
    {
        if (!_providers.Remove(providerName, out var entry)) return;
        // BrowserClient 종료는 UI 스레드 합류를 몇 초 기다릴 수 있어 백그라운드에서 정리한다
        var p = entry.Provider;
        Task.Run(() => { try { p.Dispose(); } catch { } });
    }

    private void ReleaseProviderIfIdle()
    {
        if (_providers.Count > 0 && !IsBusy && DateTime.Now - _providerLastUsed > ProviderIdleTimeout)
            ReleaseProvider();
    }

    // ---------- 동기화 ----------

    /// <summary>버튼·F5: 작가 탭에서 보고 있는 사이트만 받는다 (다른 사이트는 건드리지 않음)</summary>
    private void BtnSync_Click(object sender, RoutedEventArgs e) => StartSync(site: _site);

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _syncCts?.Cancel();
        TxtStatus.Text = "중지하는 중...";
        BtnStop.IsEnabled = false;
    }

    private void BtnRedownload_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        if (SelectedRow is not { IsMapped: true } row)
        {
            ShowInfo("작가 탭에서 다시 받을 작가를 먼저 선택하세요.");
            return;
        }
        var dlg = new RedownloadDialog(this, row.FolderName, row.CreatorId);
        if (dlg.ShowDialog() != true) return;
        StartSync(
            new Dictionary<string, DateTime> { [row.CreatorId] = dlg.Since },
            new HashSet<string> { row.CreatorId },
            row.Provider);
    }

    /// <param name="site">지정하면 그 사이트만 동기화 (버튼·F5·다시 받기). null이면 로그인된 모든 사이트 (트레이 메뉴·자동 스케줄)</param>
    private async void StartSync(Dictionary<string, DateTime>? reDownloadSince = null,
        HashSet<string>? onlyCreators = null, string? site = null)
    {
        if (IsBusy) return;
        if (site != null ? _settings.ActiveSite(site) == null : _settings.ActiveSites.Count == 0)
        {
            ShowInfo(site != null
                ? $"설정 탭에서 {CreatorIds.ProviderLabel(site)} 쿠키를 먼저 입력하세요."
                : "설정 탭에서 FANBOXSESSID 또는 Patreon session_id 쿠키를 먼저 입력하세요.");
            Tabs.SelectedIndex = 1;
            if (site != null) SelectSite(site);
            return;
        }

        _syncRunning = true;
        _syncCts = new CancellationTokenSource();
        SetSyncButtons(running: true);
        Progress.Value = 0;
        TxtStatus.Text = site != null ? $"{CreatorIds.ProviderLabel(site)} 동기화 시작..." : "동기화 시작 (모든 사이트)...";

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
            if (row == null) return;
            row.LastResult = newCount > 0 ? $"새 게시물 {newCount}건 ({DateTime.Now:HH:mm})" : $"변경 없음 ({DateTime.Now:HH:mm})";
            row.DownloadedCount = _state.CountFor(creatorId);
        });

        try
        {
            // 프로바이더는 동기화가 끝나도 유지한다 (유휴 5분 뒤 자동 해제) — 연속 작업 때 WebView2 재기동을 피함
            var providers = await AcquireProvidersAsync(_syncCts.Token, site);
            if (providers == null) return;
            var summary = await engine.RunAsync(providers, _syncCts.Token, reDownloadSince, onlyCreators, site);
            _providerLastUsed = DateTime.Now;

            Progress.Value = 100;
            TxtStatus.Text = summary.Cancelled
                ? $"중지됨 — 새 게시물 {summary.NewPosts}건 저장"
                : $"완료 — 새 게시물 {summary.NewPosts}건" +
                  (summary.LockedSkipped > 0 ? $", 열람 불가 {summary.LockedSkipped}건" : "") +
                  (summary.Errors > 0 ? $", 오류 {summary.Errors}건" : "");
            Notify(TxtStatus.Text);

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
            ReleaseProvider(); // 세션 만료·브라우저 오류 등은 다음에 새로 띄워서 복구
            ShowInfo($"동기화에 실패했습니다.\n\n{ex.Message}");
        }
        finally
        {
            _syncRunning = false;
            SetSyncButtons(running: false);
            _syncCts?.Dispose();
            _syncCts = null;
        }
    }

    private void SetSyncButtons(bool running)
    {
        BtnSync.IsEnabled = !running;
        BtnRefresh.IsEnabled = !running;
        BtnRedownload.IsEnabled = !running;
        BtnTestSession.IsEnabled = !running;
        BtnStop.IsEnabled = running;
    }

    private void SaveState() => _state.Save(App.StatePath);

    // ---------- 스케줄러 ----------

    private void CheckSchedule()
    {
        if (!_settings.Schedule.Enabled || IsBusy) return;
        var next = _settings.Schedule.NextRunAfter(DateTime.Now);
        if (next != null && DateTime.Now >= next)
            StartSync();
    }

    private void UpdateNextRunText()
    {
        var next = _settings.Schedule.NextRunAfter(DateTime.Now);
        TxtNextRun.Text = next == null ? "" :
            next <= DateTime.Now ? "자동 실행: 곧 시작" : $"다음 자동 실행: {next:MM-dd HH:mm}";
    }
}
