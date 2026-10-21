using System.Windows;

namespace Updater.App;

/// <summary>자동 업데이트 — 시작 시 + 6시간마다 확인, 버튼 클릭으로 적용</summary>
public partial class MainWindow
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateService _updater = new();
    private Velopack.UpdateInfo? _pendingUpdate;
    private DateTime _lastUpdateCheck = DateTime.MinValue;

    private async Task CheckForUpdatesAsync()
    {
        _lastUpdateCheck = DateTime.Now;
        if (_pendingUpdate != null) return; // 이미 버튼이 떠 있음
        try
        {
            _pendingUpdate = await _updater.CheckAsync();
            if (_pendingUpdate == null) return;
            var ver = _pendingUpdate.TargetFullRelease.Version;
            BtnUpdate.Content = $"⬆ v{ver} 업데이트";
            BtnUpdate.Visibility = Visibility.Visible;
            AppendLog($"새 버전 v{ver}이(가) 있습니다. 상단 업데이트 버튼으로 설치하세요.");
        }
        catch { /* 오프라인/개발 모드 등 — 조용히 무시 */ }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        if (IsBusy)
        {
            ShowInfo("동기화가 끝난 뒤 업데이트하세요.");
            return;
        }
        BtnUpdate.IsEnabled = false;
        BtnSync.IsEnabled = false;
        Progress.Value = 0;
        ShowUpdateOverlay(0);
        try
        {
            ReleaseProvider();
            await _updater.DownloadAndApplyAsync(_pendingUpdate, p => Dispatcher.BeginInvoke(() =>
            {
                Progress.Value = p;
                ShowUpdateOverlay(p);
            }), beforeRestart: () => Dispatcher.Invoke(() =>
            {
                // 재시작 직전: 트레이 아이콘을 지워야 유령 아이콘이 안 남고, 창 닫힘이 트레이 이동으로 바뀌지 않게 한다
                _reallyExit = true;
                _tray?.Dispose();
                _tray = null;
            }));
        }
        catch (Exception ex)
        {
            UpdateOverlay.Visibility = Visibility.Collapsed;
            BtnUpdate.IsEnabled = true;
            BtnSync.IsEnabled = true;
            Progress.Value = 0;
            TxtStatus.Text = "대기 중";
            _reallyExit = false;
            ShowInfo($"업데이트 실패: {ex.Message}");
        }
    }

    /// <summary>씹덕 감성 업데이트 오버레이 — 진행률에 따라 표정과 멘트가 바뀐다</summary>
    private void ShowUpdateOverlay(int p)
    {
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateProgress.Value = p;
        TxtUpdatePct.Text = $"{p}%";
        var (face, msg) = p switch
        {
            >= 100 => ("(ﾉ≧∀≦)ﾉ ☆", "완료! 새 모습으로 재시작할게요~"),
            >= 70 => ("(≧▽≦)/", "거의 다 왔어요!"),
            >= 30 => ("(ﾉ´ヮ`)ﾉ*:･ﾟ✧", "조금만 더~ 열심히 받는 중!"),
            _ => ("(´｡• ᵕ •｡`)", "새 버전을 데려오는 중이에요..."),
        };
        TxtUpdateFace.Text = face;
        TxtUpdateMsg.Text = msg;
        TxtStatus.Text = p >= 100 ? "업데이트 적용 중 — 곧 재시작합니다" : $"업데이트 다운로드 중... {p}%";
    }
}
