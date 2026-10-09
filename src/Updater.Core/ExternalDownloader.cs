using System.Net;
using System.Text.RegularExpressions;

namespace Updater.Core;

/// <summary>
/// 게시물 본문의 외부 링크(iframely 카드 등)를 실제 URL로 해석하고,
/// 지원 호스트(Dropbox, Google Drive 공개파일, 직접 파일 링크)는 자동 다운로드한다.
/// </summary>
public static class ExternalDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36");
        return http;
    }

    /// <summary>iframely 등 링크 카드 URL을 실제 목적지 URL로 해석. 실패하면 원본 반환.</summary>
    public static async Task<string> ResolveAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!url.Contains("iframely.net/", StringComparison.OrdinalIgnoreCase))
                return url;
            var html = await Http.GetStringAsync(url, ct);
            // og:url 또는 canonical에 실제 링크가 들어 있음
            var m = Regex.Match(html, @"property=""og:url""[^>]*content=""([^""]+)""");
            if (!m.Success) m = Regex.Match(html, @"content=""(https?://[^""]+)""[^>]*property=""og:url""");
            if (!m.Success) m = Regex.Match(html, @"rel=""canonical""[^>]*href=""(https?://[^""]+)""");
            if (!m.Success) m = Regex.Match(html, @"content=""(https?://(?!iframely)[^""]+)""");
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : url;
        }
        catch { return url; }
    }

    private static readonly string[] DirectExtensions =
        { ".mp4", ".webm", ".mov", ".avi", ".mkv", ".gif", ".png", ".jpg", ".jpeg", ".webp",
          ".zip", ".rar", ".7z", ".pdf", ".psd", ".clip", ".mp3", ".wav" };

    /// <summary>지원 호스트면 다운로드하고 true. 미지원이면 false (링크만 기록).</summary>
    public static async Task<bool> TryDownloadAsync(string url, string destDir, Action<string>? log, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();

        string downloadUrl;
        string? fallbackName = null;

        if (host.EndsWith("dropbox.com"))
        {
            // dl=1로 바꾸면 직접 다운로드
            var b = new UriBuilder(uri);
            b.Query = b.Query.Contains("dl=0") ? b.Query.TrimStart('?').Replace("dl=0", "dl=1")
                    : b.Query.Contains("dl=1") ? b.Query.TrimStart('?')
                    : (b.Query.TrimStart('?') + "&dl=1").TrimStart('&');
            downloadUrl = b.Uri.ToString();
            fallbackName = WebUtility.UrlDecode(uri.Segments.Last().Trim('/'));
        }
        else if (host == "drive.google.com")
        {
            // 공개 파일: /file/d/{id}/ → uc?export=download
            var m = Regex.Match(uri.AbsolutePath, @"/file/d/([^/]+)");
            if (!m.Success) return false;
            downloadUrl = $"https://drive.google.com/uc?export=download&id={m.Groups[1].Value}&confirm=t";
        }
        else if (DirectExtensions.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            downloadUrl = url;
            fallbackName = WebUtility.UrlDecode(uri.Segments.Last().Trim('/'));
        }
        else
        {
            return false; // MEGA, Discord 등 미지원
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var res = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode) return false;
                var contentType = res.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.StartsWith("text/html")) return false; // 파일이 아니라 페이지가 옴

                var name = res.Content.Headers.ContentDisposition?.FileNameStar
                        ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                        ?? fallbackName ?? "external_file";
                name = NameSanitizer.Sanitize(WebUtility.UrlDecode(name));
                var dest = Path.Combine(destDir, name);
                if (File.Exists(dest)) return true;

                var tmp = dest + ".part";
                await using (var fs = File.Create(tmp))
                    await res.Content.CopyToAsync(fs, ct);
                File.Move(tmp, dest, overwrite: true);
                var mb = Math.Round(new FileInfo(dest).Length / 1048576.0, 1);
                log?.Invoke($"  [외부 다운로드] {name} ({mb} MB)");
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (attempt <= 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct);
            }
            catch { return false; }
        }
    }
}
