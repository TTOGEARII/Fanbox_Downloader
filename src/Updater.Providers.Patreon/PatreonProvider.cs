using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Updater.Core;
using Updater.Providers.Browser;

namespace Updater.Providers.Patreon;

/// <summary>
/// Patreon 프로바이더. 후원(pledge) 중인 캠페인 목록과 캠페인별 게시물을 www.patreon.com/api (JSON:API)로 가져온다.
/// patreon.com도 Cloudflare가 일반 HTTP 클라이언트를 차단하므로 API 호출은 숨긴 WebView2(BrowserClient)로 한다.
/// creatorId는 "patreon:{vanity}" (주소 patreon.com/{vanity}의 vanity) — vanity가 없는 캠페인은 "patreon:{campaignId}".
/// 미리보기(티저·썸네일·post.image)는 받지 않고, 게시물에 실제로 첨부된 이미지·미디어·첨부파일만 받는다.
/// </summary>
public partial class PatreonProvider : ISiteProvider
{
    private const string Root = "https://www.patreon.com";
    private const string ProviderName = AppSettings.PatreonProvider;

    private readonly HttpClient _http;
    private readonly BrowserClient _browser;
    private readonly int _delayMs;
    private readonly SemaphoreSlim _apiLock = new(1); // API 호출 직렬화 + 간격 제한
    private DateTime _lastRequest = DateTime.MinValue;

    /// <summary>vanity(또는 campaignId) → campaignId. 작가 목록 조회 때 채워지고, 없으면 작가 페이지에서 찾아 넣는다.</summary>
    private readonly Dictionary<string, string> _campaignIds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>postId → 목록 조회 때 이미 파싱해 둔 상세 내용 (게시물 상세 API를 따로 부르지 않게)</summary>
    private readonly Dictionary<string, PostContent> _contentCache = new();

    private static readonly IReadOnlyDictionary<string, string> ApiHeaders = new Dictionary<string, string>
    {
        ["Accept"] = "application/json",
        ["Content-Type"] = "application/vnd.api+json",
    };

    public string Name => ProviderName;

    public PatreonProvider(string sessionId, int requestDelayMs, string userDataFolder)
    {
        _delayMs = requestDelayMs;
        _browser = new BrowserClient($"{Root}/home",
            new[] { new BrowserCookie("session_id", sessionId, ".patreon.com") }, userDataFolder);

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
        };
        handler.CookieContainer.Add(new Cookie("session_id", sessionId, "/", ".patreon.com"));
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Referer", $"{Root}/");
    }

    public Task InitializeAsync(CancellationToken ct) => _browser.ReadyTask.WaitAsync(ct);

    // ---------- API (WebView2 경유) ----------

    private async Task<string> GetStringAsync(string url, CancellationToken ct, bool html = false)
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

                var res = await _browser.FetchAsync(url, ct,
                    headers: html ? new Dictionary<string, string> { ["Accept"] = "text/html" } : ApiHeaders);
                if (res.Status == 429 && attempt <= 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                if (res.Status == 401)
                    throw new InvalidOperationException(
                        "Patreon 인증 실패 (401). session_id 쿠키가 만료되었습니다. 설정에서 갱신하세요.");
                if (res.Status == 403 && attempt <= 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                    continue;
                }
                if (res.Status == 403)
                    throw new InvalidOperationException(
                        "Patreon 접근 거부 (403). session_id 쿠키가 만료되었거나 Cloudflare 검사에 걸렸습니다. 설정에서 쿠키를 갱신하세요.");
                if (res.Status is < 200 or >= 300)
                    throw new HttpRequestException($"API 요청 실패 (status {res.Status}): {url}");
                return res.Body;
            }
        }
        finally { _apiLock.Release(); }
    }

    // ---------- 후원 작가 목록 ----------

    /// <summary>후원(pledge) 중인 캠페인 목록. 엔드포인트가 바뀌어도 동작하도록 두 가지를 차례로 시도한다.</summary>
    public async Task<List<CreatorInfo>> GetFollowedCreatorsAsync(CancellationToken ct)
    {
        const string campaignFields = "fields[campaign]=name,url,avatar_photo_url";
        var endpoints = new[]
        {
            $"{Root}/api/pledges?include=campaign&{campaignFields}&fields[pledge]=amount_cents,status&json-api-version=1.0",
            $"{Root}/api/current_user?include=pledges.campaign&{campaignFields}&fields[pledge]=amount_cents&fields[user]=full_name&json-api-use-default-includes=false&json-api-version=1.0",
        };

        var result = new Dictionary<string, CreatorInfo>(StringComparer.OrdinalIgnoreCase);
        string? lastError = null;
        foreach (var url in endpoints)
        {
            string json;
            try { json = await GetStringAsync(url, ct); }
            catch (InvalidOperationException) { throw; } // 인증 실패는 바로 알림
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = ex.Message; continue; }

            using var doc = JsonDocument.Parse(json);
            foreach (var el in Included(doc.RootElement, "campaign"))
            {
                var info = ParseCampaign(el);
                if (info != null) result[info.CreatorId] = info;
            }
            if (result.Count > 0) break;
        }
        if (result.Count == 0 && lastError != null)
            throw new HttpRequestException($"후원 캠페인 목록을 가져오지 못했습니다: {lastError}");
        return result.Values.ToList();
    }

    private CreatorInfo? ParseCampaign(JsonElement campaign)
    {
        var id = campaign.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;
        var attrs = Attrs(campaign);
        var name = Str(attrs, "name");
        var vanity = Str(attrs, "vanity");
        if (string.IsNullOrEmpty(vanity))
            vanity = VanityFromUrl(Str(attrs, "url"));
        var local = string.IsNullOrEmpty(vanity) ? id : vanity;
        _campaignIds[local] = id;
        _campaignIds[id] = id;
        return new CreatorInfo(ProviderName, CreatorIds.Qualify(ProviderName, local), string.IsNullOrEmpty(name) ? local : name);
    }

    /// <summary>https://www.patreon.com/xxxx → xxxx. /user?u=…, /c/xxxx 등 vanity가 아닌 주소는 null.</summary>
    private static string? VanityFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 2 && segs[0] is "c" or "cw") return segs[1];
        if (segs.Length != 1) return null;
        var v = segs[0];
        return v is "user" or "home" or "posts" or "campaign" or "join" or "checkout" ? null : v;
    }

    private async Task<string> ResolveCampaignIdAsync(string local, CancellationToken ct)
    {
        if (_campaignIds.TryGetValue(local, out var cached)) return cached;
        if (local.All(char.IsAsciiDigit)) return local;

        // 수동 입력된 vanity — 작가 페이지 HTML에서 캠페인 ID를 찾는다
        var html = await GetStringAsync($"{Root}/{Uri.EscapeDataString(local)}", ct, html: true);
        var m = CampaignIdRegex().Match(html);
        if (!m.Success)
            throw new InvalidOperationException($"Patreon 작가 페이지에서 캠페인 ID를 찾지 못했습니다: {local} (주소의 이름이 맞는지 확인하세요)");
        var id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        _campaignIds[local] = id;
        return id;
    }

    [GeneratedRegex(@"(?:""campaign""\s*:\s*\{\s*""data""\s*:\s*\{\s*""id""\s*:\s*""(\d+)"")|(?:/api/campaigns/(\d+))|(?:campaign_id(?:\\?""|&quot;)?\s*:\s*(?:\\?""|&quot;)?(\d+))")]
    private static partial Regex CampaignIdRegex();

    // ---------- 게시물 목록 ----------

    private const string PostIncludeQuery =
        "include=campaign,access_rules,attachments,attachments_media,audio,images,media,user" +
        "&fields[campaign]=name,url" +
        "&fields[post]=content,current_user_can_view,embed,is_paid,min_cents_pledged_to_view,post_file,post_metadata," +
        "published_at,patreon_url,post_type,title,url" +
        "&fields[access_rule]=access_rule_type,amount_cents" +
        "&fields[media]=id,image_urls,download_url,metadata,file_name" +
        "&fields[user]=full_name" +
        "&json-api-use-default-includes=false&json-api-version=1.0";

    private static string PostsUrl(string campaignId) =>
        $"{Root}/api/posts?{PostIncludeQuery}" +
        $"&filter[campaign_id]={Uri.EscapeDataString(campaignId)}&filter[contains_exclusive_posts]=true&filter[is_draft]=false" +
        "&sort=-published_at";

    public async Task<List<RemotePost>> GetPostsAsync(string creatorId, CancellationToken ct,
        Func<RemotePost, bool>? isSettled = null)
    {
        var campaignId = await ResolveCampaignIdAsync(CreatorIds.LocalId(creatorId), ct);
        _contentCache.Clear(); // 작가 하나씩 처리하므로 이전 작가 내용은 버린다
        var posts = new List<RemotePost>();
        var url = PostsUrl(campaignId);

        // 페이지는 최신 → 과거 순. 한 페이지가 전부 처리된 게시물이면 그보다 오래된 페이지도 처리된 것으로 보고 중단한다.
        for (int page = 0; url != null && page < 1000; page++)
        {
            var json = await GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var included = IndexIncluded(root);
            bool allSettled = isSettled != null;
            int pageCount = 0;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in data.EnumerateArray())
                {
                    var (post, content) = ParsePost(creatorId, el, included);
                    if (post == null) continue;
                    if (content != null) _contentCache[post.PostId] = content;
                    posts.Add(post);
                    pageCount++;
                    if (allSettled && !isSettled!(post)) allSettled = false;
                }
            }
            if (allSettled && pageCount > 0) break;

            url = root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
                  && links.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString() : null;
            if (pageCount == 0) break;
        }
        return posts;
    }

    public async Task<PostContent?> GetPostContentAsync(RemotePost post, CancellationToken ct)
    {
        if (_contentCache.TryGetValue(post.PostId, out var cached)) return cached;

        // 목록 캐시에 없으면(드문 경우) 게시물 하나를 직접 조회
        var url = $"{Root}/api/posts/{Uri.EscapeDataString(post.PostId)}?{PostIncludeQuery}";
        var json = await GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        var (_, content) = ParsePost(post.CreatorId, data, IndexIncluded(doc.RootElement));
        return content;
    }

    // ---------- 파싱 ----------

    /// <summary>included[] 를 "type:id" → 요소로 색인</summary>
    private static Dictionary<string, JsonElement> IndexIncluded(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>();
        if (!root.TryGetProperty("included", out var inc) || inc.ValueKind != JsonValueKind.Array) return map;
        foreach (var el in inc.EnumerateArray())
        {
            var type = Str(el, "type");
            var id = Str(el, "id");
            if (type != null && id != null) map[$"{type}:{id}"] = el;
        }
        return map;
    }

    private static IEnumerable<JsonElement> Included(JsonElement root, string type)
    {
        if (!root.TryGetProperty("included", out var inc) || inc.ValueKind != JsonValueKind.Array) yield break;
        foreach (var el in inc.EnumerateArray())
            if (Str(el, "type") == type) yield return el;
    }

    /// <summary>relationships.{name}.data[] 의 id 목록을 included에서 찾아 돌려준다 (순서 유지)</summary>
    private static List<(string Id, JsonElement El)> Related(JsonElement post, string name, Dictionary<string, JsonElement> included)
    {
        var list = new List<(string, JsonElement)>();
        if (!post.TryGetProperty("relationships", out var rels) || rels.ValueKind != JsonValueKind.Object) return list;
        if (!rels.TryGetProperty(name, out var rel) || rel.ValueKind != JsonValueKind.Object) return list;
        if (!rel.TryGetProperty("data", out var data)) return list;
        IEnumerable<JsonElement> refs = data.ValueKind switch
        {
            JsonValueKind.Array => data.EnumerateArray(),
            JsonValueKind.Object => new[] { data },
            _ => Array.Empty<JsonElement>(),
        };
        foreach (var r in refs)
        {
            var type = Str(r, "type");
            var id = Str(r, "id");
            if (type != null && id != null && included.TryGetValue($"{type}:{id}", out var el))
                list.Add((id, el));
        }
        return list;
    }

    private (RemotePost? Post, PostContent? Content) ParsePost(string creatorId, JsonElement el, Dictionary<string, JsonElement> included)
    {
        var id = Str(el, "id");
        if (string.IsNullOrEmpty(id)) return (null, null);
        var attrs = Attrs(el);
        var title = Str(attrs, "title") ?? "";
        var published = ParseDate(attrs, "published_at");
        var canView = !attrs.TryGetProperty("current_user_can_view", out var cv) || cv.ValueKind != JsonValueKind.False;

        // 유료 여부: 최소 후원액 > 0 이거나, 접근 규칙에 public이 없으면 후원자 전용(유료)으로 본다
        int fee = attrs.TryGetProperty("min_cents_pledged_to_view", out var mc) && mc.ValueKind == JsonValueKind.Number
            ? mc.GetInt32() : 0;
        var rules = Related(el, "access_rules", included);
        if (fee <= 0 && rules.Count > 0)
        {
            bool isPublic = rules.Any(r => Str(Attrs(r.El), "access_rule_type") == "public");
            if (!isPublic)
            {
                var max = rules.Select(r => Attrs(r.El).TryGetProperty("amount_cents", out var ac) && ac.ValueKind == JsonValueKind.Number ? ac.GetInt32() : 0).Max();
                fee = max > 0 ? max : 1;
            }
        }
        var post = new RemotePost(creatorId, id, title, published, canView, fee);
        if (!canView) return (post, null);

        var content = new PostContent
        {
            Title = title,
            PublishedAt = published,
            SourceUrl = Str(attrs, "url") is { Length: > 0 } u ? (u.StartsWith("http") ? u : Root + u) : $"{Root}/posts/{id}",
            CoverImageUrl = null, // 미리보기 이미지는 받지 않는다
        };

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMediaIds = new HashSet<string>();
        int index = 0;

        void AddMedia(string mediaId, JsonElement media, FileKind? forceKind = null)
        {
            if (!seenMediaIds.Add(mediaId)) return;
            var ma = Attrs(media);
            var url = Str(ma, "download_url");
            if (string.IsNullOrEmpty(url) && ma.TryGetProperty("image_urls", out var iu) && iu.ValueKind == JsonValueKind.Object)
                url = Str(iu, "original") ?? Str(iu, "default") ?? Str(iu, "url");
            if (string.IsNullOrEmpty(url) || !seenKeys.Add(MediaKey(url))) return;
            var name = Str(ma, "file_name");
            var ext = ExtOf(name) ?? ExtOf(PathOf(url)) ?? "jpg";
            var baseName = string.IsNullOrWhiteSpace(name) ? "" : Path.GetFileNameWithoutExtension(name);
            var fileName = string.IsNullOrWhiteSpace(baseName) ? $"{++index:D3}.{ext}" : $"{++index:D3}_{baseName}.{ext}";
            content.Items.Add(new DownloadItem(url, fileName, forceKind ?? FileKinds.FromExtension(ext)));
        }

        // 1) 이미지 갤러리 — post_metadata.image_order 가 있으면 그 순서대로
        var images = Related(el, "images", included);
        if (attrs.TryGetProperty("post_metadata", out var pm) && pm.ValueKind == JsonValueKind.Object
            && pm.TryGetProperty("image_order", out var order) && order.ValueKind == JsonValueKind.Array)
        {
            var byId = images.ToDictionary(i => i.Id, i => i.El);
            foreach (var o in order.EnumerateArray())
                if (o.ValueKind == JsonValueKind.String && o.GetString() is { } oid && byId.TryGetValue(oid, out var img))
                    AddMedia(oid, img);
        }
        foreach (var (mid, img) in images) AddMedia(mid, img);
        // 2) 그 밖의 미디어(영상·오디오 등)
        foreach (var (mid, m) in Related(el, "media", included)) AddMedia(mid, m);
        foreach (var (mid, m) in Related(el, "audio", included)) AddMedia(mid, m);
        // 3) 첨부파일
        foreach (var (mid, m) in Related(el, "attachments_media", included)) AddMedia(mid, m);
        foreach (var (_, att) in Related(el, "attachments", included))
        {
            var aa = Attrs(att);
            var url = Str(aa, "url");
            if (string.IsNullOrEmpty(url) || !seenKeys.Add(MediaKey(url))) continue;
            if (url.StartsWith('/')) url = Root + url;
            var name = Str(aa, "name");
            if (string.IsNullOrWhiteSpace(name)) name = "attachment_" + (content.Items.Count + 1);
            content.Items.Add(new DownloadItem(url, name, FileKinds.FromExtension(ExtOf(name))));
        }

        var texts = new List<string>();
        // 4) post_file (파일/영상 게시물)
        if (attrs.TryGetProperty("post_file", out var pf) && pf.ValueKind == JsonValueKind.Object)
        {
            var url = Str(pf, "url");
            if (!string.IsNullOrEmpty(url) && seenKeys.Add(MediaKey(url)))
            {
                var name = Str(pf, "name");
                var ext = ExtOf(name) ?? ExtOf(PathOf(url));
                if (string.Equals(ext, "m3u8", StringComparison.OrdinalIgnoreCase) || PathOf(url).Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    texts.Add($"[영상 스트림 — 직접 다운로드 미지원] {url}");
                else
                {
                    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(PathOf(url));
                    if (string.IsNullOrWhiteSpace(name)) name = "file";
                    if (ExtOf(name) == null && ext != null) name += "." + ext;
                    content.Items.Add(new DownloadItem(url, name, FileKinds.FromExtension(ExtOf(name))));
                }
            }
        }

        // 5) 본문: 텍스트 추출 + 인라인 이미지(갤러리와 중복 아닌 것만) + 외부 링크
        var html = Str(attrs, "content");
        if (!string.IsNullOrWhiteSpace(html))
        {
            foreach (Match m in ImgSrcRegex().Matches(html))
            {
                var src = WebUtility.HtmlDecode(m.Groups[1].Value);
                if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenKeys.Add(MediaKey(src))) continue;
                var ext = ExtOf(PathOf(src)) ?? "jpg";
                content.Items.Add(new DownloadItem(src, $"{++index:D3}.{ext}", FileKind.Image));
            }
            foreach (Match m in HrefRegex().Matches(html))
            {
                var href = WebUtility.HtmlDecode(m.Groups[1].Value);
                if (!Uri.TryCreate(href, UriKind.Absolute, out var hu)) continue;
                if (hu.Host.EndsWith("patreon.com", StringComparison.OrdinalIgnoreCase)
                    || hu.Host.EndsWith("patreonusercontent.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (!content.ExternalLinks.Contains(href, StringComparer.OrdinalIgnoreCase)) content.ExternalLinks.Add(href);
            }
            var text = HtmlToText(html);
            if (text.Length > 0) texts.Insert(0, text);
        }
        if (attrs.TryGetProperty("embed", out var embed) && embed.ValueKind == JsonValueKind.Object)
        {
            var eu = Str(embed, "url");
            if (!string.IsNullOrEmpty(eu) && !content.ExternalLinks.Contains(eu, StringComparer.OrdinalIgnoreCase))
                content.ExternalLinks.Add(eu);
        }
        if (texts.Count > 0) content.Text = string.Join("\n\n", texts);
        return (post, content);
    }

    /// <summary>같은 미디어의 다른 크기/서명 URL을 하나로 묶는 키.
    /// patreonusercontent 주소는 /patreon-media/p/post/{postId}/{hash}/ 까지가 미디어 식별자다.</summary>
    private static string MediaKey(string url)
    {
        var m = MediaPathRegex().Match(url);
        return m.Success ? m.Groups[1].Value : PathOf(url);
    }

    private static string PathOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url.Split('?')[0];

    private static string? ExtOf(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var ext = Path.GetExtension(name).TrimStart('.');
        return ext.Length is >= 1 and <= 5 && ext.All(char.IsLetterOrDigit) ? ext.ToLowerInvariant() : null;
    }

    private static string HtmlToText(string html)
    {
        var s = BrRegex().Replace(html, "\n");
        s = BlockEndRegex().Replace(s, "\n");
        s = TagRegex().Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace("\r", "");
        s = MultiNewlineRegex().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex("""(/patreon-media/p/post/\d+/[0-9a-fA-F]+)/""")]
    private static partial Regex MediaPathRegex();
    [GeneratedRegex(@"<img[^>]+src\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();
    [GeneratedRegex(@"<a[^>]+href\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
    [GeneratedRegex("""<br\s*/?>""", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();
    [GeneratedRegex("""</(?:p|div|li|h[1-6]|tr|blockquote)>""", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();
    [GeneratedRegex("""<[^>]+>""")]
    private static partial Regex TagRegex();
    [GeneratedRegex("""\n{3,}""")]
    private static partial Regex MultiNewlineRegex();

    // ---------- 파일 다운로드 ----------

    /// <summary>미디어 CDN(patreonusercontent)은 일반 HttpClient로 받는다. patreon.com/file?… 첨부 링크처럼
    /// Cloudflare에 막히면(403) 브라우저 다운로드로 대체한다.</summary>
    public async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        try
        {
            await DownloadHttpAsync(url, destPath, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
                                              && IsPatreonHost(url))
        {
            var resolved = await _browser.DownloadAsync(url, destPath, ct);
            if (resolved != null) await DownloadHttpAsync(resolved, destPath, ct);
        }
    }

    private static bool IsPatreonHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("patreon.com", StringComparison.OrdinalIgnoreCase);

    private async Task DownloadHttpAsync(string url, string destPath, CancellationToken ct)
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
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw; // 재시도해도 같은 결과 — 상위에서 브라우저 대체 여부 결정
            }
            catch (Exception) when (attempt <= 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }

    // ---------- 헬퍼 ----------

    /// <summary>속성이 없을 때 돌려줄 빈 객체 — default(JsonElement)는 TryGetProperty가 예외를 던지므로 쓰지 않는다</summary>
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private static JsonElement Attrs(JsonElement el)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object
            ? a : EmptyObject;

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static DateTime ParseDate(JsonElement el, string prop)
        => Str(el, prop) is { } s
           && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt : DateTime.MinValue;

    public void Dispose()
    {
        _http.Dispose();
        _browser.Dispose();
        _apiLock.Dispose();
    }
}
