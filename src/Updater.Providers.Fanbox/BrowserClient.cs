using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Updater.Providers.Fanbox;

public record FetchResult(int Status, string Body);

/// <summary>
/// api.fanbox.cc의 일부 엔드포인트(post.info 등)는 Cloudflare가 일반 HTTP 클라이언트의
/// TLS 지문을 차단하므로, 숨겨진 WebView2(엣지 엔진)에서 fetch()를 실행해 실제 브라우저로 호출한다.
/// </summary>
public class BrowserClient : IDisposable
{
    private Form? _form;
    private WebView2? _webView;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FetchResult>> _pending = new();
    private int _idCounter;

    public BrowserClient(string sessionId, string userDataFolder)
    {
        _thread = new Thread(() => RunUiThread(sessionId, userDataFolder));
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    public Task ReadyTask => _ready.Task;

    private void RunUiThread(string sessionId, string userDataFolder)
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
                    var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                    await _webView.EnsureCoreWebView2Async(env);
                    var core = _webView.CoreWebView2;

                    var cookie = core.CookieManager.CreateCookie("FANBOXSESSID", sessionId, ".fanbox.cc", "/");
                    cookie.IsSecure = true;
                    cookie.IsHttpOnly = true;
                    core.CookieManager.AddOrUpdateCookie(cookie);

                    core.WebMessageReceived += OnWebMessage;
                    core.NavigationCompleted += (_, e) =>
                    {
                        if (e.IsSuccess) _ready.TrySetResult();
                        else if (!_ready.Task.IsCompleted)
                            _ready.TrySetException(new InvalidOperationException(
                                $"fanbox.cc 페이지 로드 실패: {e.WebErrorStatus}"));
                    };
                    core.Navigate("https://www.fanbox.cc/");
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
    public async Task<FetchResult> FetchAsync(string url, CancellationToken ct, TimeSpan? timeout = null)
    {
        await _ready.Task.WaitAsync(ct);
        var id = Interlocked.Increment(ref _idCounter).ToString();
        var tcs = new TaskCompletionSource<FetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var urlJson = JsonSerializer.Serialize(url);
        var script = $$"""
            (async () => {
              try {
                const r = await fetch({{urlJson}}, { credentials: 'include', headers: { 'Accept': 'application/json' } });
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

    public void Dispose()
    {
        try
        {
            if (_form is { IsHandleCreated: true })
                _form.BeginInvoke(() => { _form.Close(); Application.ExitThread(); });
            _thread.Join(TimeSpan.FromSeconds(5));
        }
        catch { }
    }
}
