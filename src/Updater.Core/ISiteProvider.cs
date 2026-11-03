namespace Updater.Core;

/// <summary>
/// 사이트별 구현 인터페이스. Fanbox 외에 Patreon 등 다른 사이트를 추가하려면
/// 이 인터페이스를 구현한 프로바이더를 만들어 ProviderRegistry에 등록하면 된다.
/// </summary>
public interface ISiteProvider : IDisposable
{
    /// <summary>프로바이더 식별자 (예: "fanbox", "patreon")</summary>
    string Name { get; }

    /// <summary>로그인 세션 준비. 실패 시 예외.</summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>팔로우/후원 중인 작가 전체 목록. CreatorId는 <see cref="CreatorIds"/> 규칙으로 사이트가 구분되는 형태여야 한다.</summary>
    Task<List<CreatorInfo>> GetFollowedCreatorsAsync(CancellationToken ct);

    /// <summary>작가의 게시물 목록 (최신순).</summary>
    /// <param name="isSettled">게시물이 더 처리할 것이 없는지(이미 받았거나 받을 필요 없음) 판정.
    /// 지정하면 한 페이지의 게시물이 전부 settled일 때 더 오래된 페이지 조회를 중단해도 된다 (API 호출 절약).</param>
    Task<List<RemotePost>> GetPostsAsync(string creatorId, CancellationToken ct, Func<RemotePost, bool>? isSettled = null);

    /// <summary>게시물 상세 내용. 열람 불가하면 null.</summary>
    Task<PostContent?> GetPostContentAsync(RemotePost post, CancellationToken ct);

    /// <summary>파일 하나를 destPath에 다운로드 (필요한 인증 헤더 포함)</summary>
    Task DownloadAsync(string url, string destPath, CancellationToken ct);
}

public record CreatorInfo(string Provider, string CreatorId, string DisplayName)
{
    /// <summary>작가 선택 창 등에 표시할 사이트 이름</summary>
    public string SiteLabel => CreatorIds.ProviderLabel(Provider);
}

/// <param name="FeeRequired">열람에 필요한 후원 금액. 0이면 무료 공개 게시물.</param>
public record RemotePost(string CreatorId, string PostId, string Title,
    DateTime PublishedAt, bool IsAccessible, int FeeRequired);

public enum FileKind { Image, Video, Archive, Other }

public record DownloadItem(string Url, string FileName, FileKind Kind);

public class PostContent
{
    public string Title { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public string? Text { get; set; }
    public string? SourceUrl { get; set; }
    public string? CoverImageUrl { get; set; }
    public List<DownloadItem> Items { get; set; } = new();
    /// <summary>본문에 걸린 외부 링크 (외부 호스트 배포 게시물 대응)</summary>
    public List<string> ExternalLinks { get; set; } = new();
}

/// <summary>
/// creatorId 표기 규칙. 설정·기록(state.json)의 모든 사전이 creatorId 하나로 키를 잡으므로
/// 사이트가 다른 작가가 같은 아이디를 쓰더라도 충돌하지 않게 사이트 접두어를 붙인다.
/// Fanbox는 기존 설정 호환을 위해 접두어 없이 그대로("creator"), 그 외 사이트는 "patreon:creator" 꼴이다.
/// </summary>
public static class CreatorIds
{
    /// <summary>creatorId가 속한 프로바이더 이름. 접두어가 없으면 fanbox.</summary>
    public static string ProviderOf(string creatorId)
    {
        var i = creatorId.IndexOf(':');
        return i > 0 ? creatorId[..i].ToLowerInvariant() : AppSettings.FanboxProvider;
    }

    /// <summary>사이트 안에서 쓰는 원래 아이디 (접두어 제거)</summary>
    public static string LocalId(string creatorId)
    {
        var i = creatorId.IndexOf(':');
        return i > 0 ? creatorId[(i + 1)..] : creatorId;
    }

    /// <summary>사이트 아이디에 접두어를 붙여 전역 creatorId로 만든다</summary>
    public static string Qualify(string provider, string localId)
        => string.Equals(provider, AppSettings.FanboxProvider, StringComparison.OrdinalIgnoreCase)
            ? localId : $"{provider.ToLowerInvariant()}:{localId}";

    /// <summary>작가 탭 표시용 사이트 이름</summary>
    public static string SiteLabel(string creatorId) => ProviderLabel(ProviderOf(creatorId));

    /// <summary>프로바이더 이름 → 표시용 사이트 이름</summary>
    public static string ProviderLabel(string provider) => provider.ToLowerInvariant() switch
    {
        AppSettings.PatreonProvider => "Patreon",
        AppSettings.FanboxProvider => "FANBOX",
        var p => p,
    };

    /// <summary>작가 페이지 주소</summary>
    public static string CreatorPageUrl(string creatorId)
    {
        var local = LocalId(creatorId);
        return ProviderOf(creatorId) switch
        {
            AppSettings.PatreonProvider => $"https://www.patreon.com/{local}",
            _ => $"https://{local}.fanbox.cc",
        };
    }
}

public static class FileKinds
{
    /// <summary>확장자(점 없이)로 파일 종류 판정</summary>
    public static FileKind FromExtension(string? ext) => (ext ?? "").TrimStart('.').ToLowerInvariant() switch
    {
        "mp4" or "mov" or "avi" or "wmv" or "webm" or "mkv" or "gif" or "m4v" => FileKind.Video,
        "zip" or "rar" or "7z" => FileKind.Archive,
        "png" or "jpg" or "jpeg" or "webp" or "psd" or "clip" or "bmp" or "tif" or "tiff" or "avif" or "heic" => FileKind.Image,
        _ => FileKind.Other,
    };
}
