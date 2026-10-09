using System.Globalization;
using System.Net;
using System.Text.Json;
using Updater.Core;

namespace Updater.Providers.Fanbox;

public class FanboxProvider : ISiteProvider
{
    private const string ApiBase = "https://api.fanbox.cc";
    private readonly HttpClient _http;
    private readonly BrowserClient _browser;
    private readonly int _delayMs;
    private readonly SemaphoreSlim _apiLock = new(1); // API 호출 직렬화 + 간격 제한
    private DateTime _lastRequest = DateTime.MinValue;

    public string Name => "fanbox";

    public FanboxProvider(string sessionId, int requestDelayMs, string userDataFolder)
    {
        _delayMs = requestDelayMs;
        _browser = new BrowserClient(sessionId, userDataFolder);

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        handler.CookieContainer.Add(new Cookie("FANBOXSESSID", sessionId, "/", ".fanbox.cc"));
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Origin", "https://www.fanbox.cc");
        _http.DefaultRequestHeaders.Add("Referer", "https://www.fanbox.cc/");
    }

    public Task InitializeAsync(CancellationToken ct) => _browser.ReadyTask.WaitAsync(ct);

    // ---------- API (WebView2 경유) ----------

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        await _apiLock.WaitAsync(ct);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                var elapsed = DateTime.UtcNow - _lastRequest;
                var wait = TimeSpan.FromMilliseconds(_delayMs) - elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastRequest = DateTime.UtcNow;

                var res = await _browser.FetchAsync(url, ct);
                if (res.Status == 429 && attempt <= 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                if (res.Status == 401)
                    throw new InvalidOperationException(
                        "인증 실패 (401). FANBOXSESSID 쿠키가 만료되었습니다. 설정에서 갱신하세요.");
                if (res.Status == 403 && attempt <= 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                    continue;
                }
                if (res.Status is < 200 or >= 300)
                    throw new HttpRequestException($"API 요청 실패 (status {res.Status}): {url}");
                return res.Body;
            }
        }
        finally { _apiLock.Release(); }
    }

    public async Task<List<CreatorInfo>> GetFollowedCreatorsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, CreatorInfo>(StringComparer.OrdinalIgnoreCase);

        var json = await GetStringAsync($"{ApiBase}/creator.listFollowing", ct);
        using (var doc = JsonDocument.Parse(json))
            foreach (var c in BodyArray(doc, "creators"))
                AddCreator(result, c);

        json = await GetStringAsync($"{ApiBase}/plan.listSupporting", ct);
        using (var doc = JsonDocument.Parse(json))
            foreach (var p in BodyArray(doc, "plans"))
                AddCreator(result, p);

        return result.Values.ToList();
    }

    private void AddCreator(Dictionary<string, CreatorInfo> map, JsonElement el)
    {
        var id = el.TryGetProperty("creatorId", out var cid) ? cid.GetString() : null;
        if (string.IsNullOrEmpty(id)) return;
        var name = el.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                   && u.TryGetProperty("name", out var n) ? n.GetString() : null;
        map[id] = new CreatorInfo(Name, id, name ?? id);
    }

    public async Task<List<RemotePost>> GetPostsAsync(string creatorId, CancellationToken ct)
    {
        var posts = new List<RemotePost>();
        var pagesJson = await GetStringAsync(
            $"{ApiBase}/post.paginateCreator?creatorId={Uri.EscapeDataString(creatorId)}", ct);
        var pageUrls = new List<string>();
        using (var doc = JsonDocument.Parse(pagesJson))
            foreach (var u in BodyArray(doc, "pageUrls"))
                if (u.GetString() is { } s) pageUrls.Add(s);

        foreach (var pageUrl in pageUrls)
        {
            var pageJson = await GetStringAsync(pageUrl, ct);
            using var doc = JsonDocument.Parse(pageJson);
            foreach (var p in BodyArray(doc, "posts"))
            {
                var id = p.GetProperty("id").GetString() ?? "";
                var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var published = ParseDate(p, "publishedDatetime");
                var restricted = p.TryGetProperty("isRestricted", out var r) && r.GetBoolean();
                var fee = p.TryGetProperty("feeRequired", out var fr) && fr.ValueKind == JsonValueKind.Number
                    ? fr.GetInt32() : 0;
                posts.Add(new RemotePost(creatorId, id, title, published, !restricted, fee));
            }
        }
        return posts;
    }

    public async Task<PostContent?> GetPostContentAsync(RemotePost post, CancellationToken ct)
    {
        var json = await GetStringAsync($"{ApiBase}/post.info?postId={Uri.EscapeDataString(post.PostId)}", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return null;
        // 응답이 body.post 로 감싸진 형태와 body 바로 아래인 형태 둘 다 처리
        var el = body.TryGetProperty("post", out var p) && p.ValueKind == JsonValueKind.Object ? p : body;
        if (!el.TryGetProperty("body", out var pb) || pb.ValueKind != JsonValueKind.Object)
            return null; // 열람 불가

        var content = new PostContent
        {
            Title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            PublishedAt = ParseDate(el, "publishedDatetime"),
            SourceUrl = $"https://www.fanbox.cc/posts/{post.PostId}",
            CoverImageUrl = el.TryGetProperty("coverImageUrl", out var cv) ? cv.GetString() : null,
        };

        var texts = new List<string>();
        if (pb.TryGetProperty("text", out var txt) && txt.GetString() is { Length: > 0 } s0)
            texts.Add(s0);

        int index = 0;

        // image / file 타입 게시물
        if (pb.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            foreach (var img in images.EnumerateArray())
                AddImage(content, img, ++index);
        if (pb.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var f in files.EnumerateArray())
                AddFile(content, f);

        // article 타입 게시물 (blocks + imageMap/fileMap)
        if (pb.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            pb.TryGetProperty("imageMap", out var imageMap);
            pb.TryGetProperty("fileMap", out var fileMap);
            foreach (var block in blocks.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                switch (type)
                {
                    case "p" or "header":
                        if (block.TryGetProperty("text", out var btx) && btx.GetString() is { } bs)
                            texts.Add(bs);
                        break;
                    case "image":
                        if (block.TryGetProperty("imageId", out var iid) && iid.GetString() is { } iids
                            && imageMap.ValueKind == JsonValueKind.Object
                            && imageMap.TryGetProperty(iids, out var img2))
                            AddImage(content, img2, ++index);
                        break;
                    case "file":
                        if (block.TryGetProperty("fileId", out var fid) && fid.GetString() is { } fids
                            && fileMap.ValueKind == JsonValueKind.Object
                            && fileMap.TryGetProperty(fids, out var f2))
                            AddFile(content, f2);
                        break;
                }
            }
        }

        if (texts.Count > 0) content.Text = string.Join("\n", texts);
        return content;
    }

    private static void AddImage(PostContent content, JsonElement img, int index)
    {
        var url = img.TryGetProperty("originalUrl", out var u) ? u.GetString() : null;
        if (url == null) return;
        var ext = img.TryGetProperty("extension", out var e) ? e.GetString() ?? "jpg" : "jpg";
        content.Items.Add(new DownloadItem(url, $"{index:D3}.{ext}", FileKind.Image));
    }

    private static void AddFile(PostContent content, JsonElement f)
    {
        var url = f.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (url == null) return;
        var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "file" : "file";
        var ext = (f.TryGetProperty("extension", out var e) ? e.GetString() ?? "" : "").ToLowerInvariant();
        var kind = ext switch
        {
            "mp4" or "mov" or "avi" or "wmv" or "webm" or "mkv" or "gif" => FileKind.Video,
            "zip" or "rar" or "7z" => FileKind.Archive,
            "png" or "jpg" or "jpeg" or "webp" or "psd" or "clip" => FileKind.Image,
            _ => FileKind.Other,
        };
        content.Items.Add(new DownloadItem(url, $"{name}.{ext}", kind));
    }

    // ---------- 파일 다운로드 (일반 HTTP — 파일 서버는 TLS 차단 없음) ----------

    public async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                var tmp = destPath + ".part";
                await using (var fs = File.Create(tmp))
                    await res.Content.CopyToAsync(fs, ct);
                File.Move(tmp, destPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (attempt <= 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }

    // ---------- 헬퍼 ----------

    /// <summary>{"body": {...prop:[...]}} 또는 {"body": [...]} 양쪽에서 배열을 꺼낸다.</summary>
    private static IEnumerable<JsonElement> BodyArray(JsonDocument doc, string prop)
    {
        if (!doc.RootElement.TryGetProperty("body", out var body)) yield break;
        JsonElement arr = default;
        if (body.ValueKind == JsonValueKind.Array) arr = body;
        else if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(prop, out var inner)
                 && inner.ValueKind == JsonValueKind.Array) arr = inner;
        else yield break;
        foreach (var el in arr.EnumerateArray()) yield return el;
    }

    private static DateTime ParseDate(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var d) && d.GetString() is { } s
           && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt : DateTime.MinValue;

    public void Dispose()
    {
        _http.Dispose();
        _browser.Dispose();
        _apiLock.Dispose();
    }
}
