namespace Updater.Core;

/// <summary>
/// 사이트별 구현 인터페이스. Fanbox 외에 Patreon 등 다른 사이트를 추가하려면
/// 이 인터페이스를 구현한 프로바이더를 만들어 ProviderRegistry에 등록하면 된다.
/// </summary>
public interface ISiteProvider : IDisposable
{
    /// <summary>프로바이더 식별자 (예: "fanbox")</summary>
    string Name { get; }

    /// <summary>로그인 세션 준비. 실패 시 예외.</summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>팔로우/후원 중인 작가 전체 목록</summary>
    Task<List<CreatorInfo>> GetFollowedCreatorsAsync(CancellationToken ct);

    /// <summary>작가의 게시물 목록 (최신순)</summary>
    Task<List<RemotePost>> GetPostsAsync(string creatorId, CancellationToken ct);

    /// <summary>게시물 상세 내용. 열람 불가하면 null.</summary>
    Task<PostContent?> GetPostContentAsync(RemotePost post, CancellationToken ct);

    /// <summary>파일 하나를 destPath에 다운로드 (필요한 인증 헤더 포함)</summary>
    Task DownloadAsync(string url, string destPath, CancellationToken ct);
}

public record CreatorInfo(string Provider, string CreatorId, string DisplayName);

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
