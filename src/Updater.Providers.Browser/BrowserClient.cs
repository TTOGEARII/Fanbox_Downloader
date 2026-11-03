using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Updater.Providers.Browser;

public record FetchResult(int Status, string Body);

/// <summary>브라우저에 심을 세션 쿠키 하나</summary>
public record BrowserCookie(string Name, string Value, string Domain);

/// <summary>
/// 숨겨진 WebView2(엣지 엔진) 안에서 사이트 API를 호출하는 공용 클라이언트.
/// api.fanbox.cc·patreon.com 등은 Cloudflare가 일반 HTTP 클라이언트의 TLS 지문을 차단하므로,
/// 실제 브라우저로 로그인 쿠키를 심고 페이지 컨텍스트에서 fetch()를 실행한다.
/// </summary>
public class BrowserClient : IDisposable
{
    private Form? _form;
    private WebView2? _webView;
    private WebView2? _downloadView;
    private CoreWebView2Environment? _env;
    private readonly string _homeUrl;
    private readonly string _siteLabel;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FetchResult>> _pending = new();
    private readonly SemaphoreSlim _downloadLock = new(1);
    private int _idCounter;

    /// <param name="homeUrl">쿠키를 심은 뒤 띄워 둘 페이지 — 이 페이지의 출처(origin)에서 fetch가 실행되므로 API와 같은 사이트여야 한다</param>
    /// <param name="cookies">심을 로그인 쿠키</param>
    /// <param name="userDataFolder">WebView2 프로필 폴더 (사이트별로 다른 폴더를 쓰는 것이 안전)</param>
    public BrowserClient(string homeUrl, IReadOnlyList<BrowserCookie> cookies, string userDataFolder)
    {
        _homeUrl = homeUrl;
        _siteLabel = new Uri(homeUrl).Host;
        _thread = new Thread(() => RunUiThread(cookies, userDataFolder));
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    public Task ReadyTask => _ready.Task;

    private void RunUiThread(IReadOnlyList<BrowserCookie> cookies, string userDataFolder)
    {
        try
        {
            _form = new Form
            {
                // 화면 밖에 두어 보이지 않게 함 (완전 숨김은 WebView2 동작이 불안정할 수 있음)
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-3000, -3000),
                Size = new Size(1024, 768),
                ShowInTaskbar = false,
            };
            _webView = new WebView2 { Dock = DockStyle.Fill };
            _form.Controls.Add(_webView);
            _form.Load += async (_, _) =>
            {
                try
                {
                    _env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                    await _webView.EnsureCoreWebView2Async(_env);
                    var core = _webView.CoreWebView2;

                    foreach (var c in cookies)
                    {
                        var cookie = core.CookieManager.CreateCookie(c.Name, c.Value, c.Domain, "/");
                        cookie.IsSecure = true;
                        cookie.IsHttpOnly = true;
                        core.CookieManager.AddOrUpdateCookie(cookie);
                    }

                    core.WebMessageReceived += OnWebMessage;
                    core.NavigationCompleted += (_, e) =>
                    {
                        if (e.IsSuccess) _ready.TrySetResult();
                        else if (!_ready.Task.IsCompleted)
                            _ready.TrySetException(new InvalidOperationException(
                                $"{_siteLabel} 페이지 로드 실패: {e.WebErrorStatus}"));
                    };
                    core.Navigate(_homeUrl);
                }
                catch (Exception ex)
                {
                    _ready.TrySetException(ex);
                }
            };
            Application.Run(_form);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = e.TryGetWebMessageAsString();
            if (msg == null) return;
            using var doc = JsonDocument.Parse(msg);
            var id = doc.RootElement.GetProperty("id").GetString() ?? "";
            var status = doc.RootElement.GetProperty("status").GetInt32();
            var body = doc.RootElement.GetProperty("body").GetString() ?? "";
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(new FetchResult(status, body));
        }
        catch { /* 형식이 다른 메시지는 무시 */ }
    }

    /// <summary>브라우저 컨텍스트에서 fetch()로 URL을 호출하고 응답 본문을 돌려받는다.</summary>
    /// <param name="headers">추가 요청 헤더 (Accept는 기본 application/json)</param>
    public async Task<FetchResult> FetchAsync(string url, CancellationToken ct, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        await _ready.Task.WaitAsync(ct);
        var id = Interlocked.Increment(ref _idCounter).ToString();
        var tcs = new TaskCompletionSource<FetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var urlJson = JsonSerializer.Serialize(url);
        var headerMap = new Dictionary<string, string> { ["Accept"] = "application/json" };
        if (headers != null) foreach (var (k, v) in headers) headerMap[k] = v;
        var headersJson = JsonSerializer.Serialize(headerMap);
        var script = $$"""
            (async () => {
              try {
                const r = await fetch({{urlJson}}, { credentials: 'include', headers: {{headersJson}} });
                const t = await r.text();
                window.chrome.webview.postMessage(JSON.stringify({ id: '{{id}}', status: r.status, body: t }));
              } catch (e) {
                window.chrome.webview.postMessage(JSON.stringify({ id: '{{id}}', status: 0, body: String(e) }));
              }
            })();
            """;

        _form!.BeginInvoke(() => _webView!.CoreWebView2.ExecuteScriptAsync(script));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        await using var reg = cts.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t))
                t.TrySetException(ct.IsCancellationRequested
                    ? new OperationCanceledException(ct)
                    : new TimeoutException($"브라우저 fetch 시간 초과: {url}"));
        });
        return await tcs.Task;
    }

    /// <summary>
    /// 브라우저로 URL을 열어 파일을 destPath에 내려받는다. 일반 HttpClient가 Cloudflare에 막히는 다운로드 링크
    /// (예: patreon.com/file?h=…) 대응. 브라우저가 파일을 바로 내려주면 그대로 저장하고,
    /// 다운로드가 아니라 이미지 등으로 그냥 표시되면 최종 URL(서명된 CDN 주소)을 돌려주어 HttpClient로 받게 한다.
    /// </summary>
    /// <returns>null이면 destPath에 저장 완료. 아니면 HttpClient로 받아야 할 최종 URL.</returns>
    public async Task<string?> DownloadAsync(string url, string destPath, CancellationToken ct, TimeSpan? timeout = null)
    {
        await _ready.Task.WaitAsync(ct);
        await _downloadLock.WaitAsync(ct);
        try
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CoreWebView2? core = null;
            EventHandler<CoreWebView2DownloadStartingEventArgs>? onDownload = null;
            EventHandler<CoreWebView2NavigationCompletedEventArgs>? onNav = null;
            bool downloadStarted = false;

            await InvokeAsync(async () =>
            {
                core = await EnsureDownloadViewAsync();
                onDownload = (_, e) =>
                {
                    downloadStarted = true;
                    e.ResultFilePath = destPath;
                    e.Handled = true; // 기본 다운로드 UI 숨김
                    var op = e.DownloadOperation;
                    op.StateChanged += (_, _) =>
                    {
                        switch (op.State)
                        {
                            case CoreWebView2DownloadState.Completed: tcs.TrySetResult(null); break;
                            case CoreWebView2DownloadState.Interrupted:
                                tcs.TrySetException(new HttpRequestException($"브라우저 다운로드 중단: {op.InterruptReason}")); break;
                        }
                    };
                };
                onNav = (_, e) =>
                {
                    if (downloadStarted) return;
                    // 다운로드가 시작되지 않고 페이지로 끝났으면: 최종 주소(CDN 리다이렉트 결과)를 넘겨 HttpClient로 받게 한다
                    var final = core!.Source;
                    if (e.IsSuccess && !string.IsNullOrEmpty(final) && final != url)
                        tcs.TrySetResult(final);
                    else if (e.IsSuccess)
                        // 리다이렉트도 다운로드도 없이 같은 주소의 문서로 끝남 (오류 페이지·Cloudflare 검사 등) — 기다리지 않고 실패
                        tcs.TrySetException(new HttpRequestException(
                            $"브라우저가 파일을 내려주지 않았습니다 (HTTP {e.HttpStatusCode}): {url}"));
                    else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.ConnectionAborted)
                        tcs.TrySetException(new HttpRequestException($"브라우저 다운로드 실패: {e.WebErrorStatus}"));
                    // ConnectionAborted는 다운로드로 전환될 때도 발생하므로 DownloadStarting을 기다린다
                };
                core.DownloadStarting += onDownload;
                core.NavigationCompleted += onNav;
                core.Navigate(url);
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
            await using var reg = cts.Token.Register(() =>
                tcs.TrySetException(ct.IsCancellationRequested
                    ? new OperationCanceledException(ct)
                    : new TimeoutException($"브라우저 다운로드 시간 초과: {url}")));
            try
            {
                return await tcs.Task;
            }
            finally
            {
                _form!.BeginInvoke(() =>
                {
                    if (core == null) return;
                    core.DownloadStarting -= onDownload;
                    core.NavigationCompleted -= onNav;
                    try { core.Navigate("about:blank"); } catch { }
                });
            }
        }
        finally { _downloadLock.Release(); }
    }

    /// <summary>다운로드 전용 두 번째 WebView — 홈 페이지(fetch 컨텍스트)를 다른 주소로 바꾸지 않기 위해 따로 둔다</summary>
    private async Task<CoreWebView2> EnsureDownloadViewAsync()
    {
        if (_downloadView?.CoreWebView2 != null) return _downloadView.CoreWebView2;
        _downloadView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        _form!.Controls.Add(_downloadView);
        await _downloadView.EnsureCoreWebView2Async(_env);
        return _downloadView.CoreWebView2;
    }

    private Task InvokeAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _form!.BeginInvoke(async () =>
        {
            try { await action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        try
        {
            if (_form is { IsHandleCreated: true })
                _form.BeginInvoke(() => { _form.Close(); Application.ExitThread(); });
            _thread.Join(TimeSpan.FromSeconds(5));
        }
        catch { }
        _downloadLock.Dispose();
    }
}
