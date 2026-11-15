namespace Updater.Core;

/// <summary>
/// 평평 저장(게시물 폴더 없이 작가 폴더에 바로 저장) 파일명 결정.
/// 제목이 같은 게시물끼리 "{title}_{index}" 이름이 겹치면 뒤에 받는 게시물의 파일이 전부 '이미 있음'으로 건너뛰어져
/// 조용히 사라지므로(🤍 같은 이모지 한 글자 제목이 반복되는 작가에서 실제 발생), 겹칠 때만 날짜 → 게시물 ID 순으로
/// 구분자를 붙인다. 겹치지 않는 보통 게시물의 이름은 사용자 템플릿 그대로.
/// </summary>
public static class FlatNamer
{
    public const string DefaultTemplate = "{title}_{index}";

    /// <param name="Items">최종 파일명이 정해진 항목</param>
    /// <param name="Renamed">템플릿 그대로가 아니라 구분자를 붙인 이름을 썼으면 true</param>
    public record Result(List<DownloadItem> Items, bool Renamed);

    /// <param name="template">사용자 템플릿 ({index}는 아직 치환되지 않은 상태)</param>
    /// <param name="fill">{title} {date} {id} {creator}를 치환하는 함수</param>
    /// <param name="exists">작가 폴더에 그 파일명이 이미 있는지</param>
    /// <param name="claimedByOther">그 파일명이 이미 받은 다른 게시물의 것으로 기록돼 있는지 (<see cref="DownloadState.FlatFiles"/>)</param>
    public static Result Resolve(IReadOnlyList<DownloadItem> items, string template, string postId,
        Func<string, string> fill, Func<string, bool> exists, Func<string, bool> claimedByOther)
    {
        if (string.IsNullOrWhiteSpace(template)) template = DefaultTemplate;
        List<DownloadItem>? last = null;
        bool renamed = false;
        foreach (var candidate in Candidates(template))
        {
            var named = Name(items, candidate, postId, fill);
            renamed = last != null; // 첫 후보(사용자 템플릿 그대로)가 아니면 구분자를 붙인 것
            last = named;
            var names = named.Select(i => i.FileName).ToList();

            // 기록상 다른 게시물의 파일과 겹침 → 다음 후보
            if (names.Any(claimedByOther)) continue;

            int existing = names.Count(exists);
            // 하나도 없으면 새로 받고, 전부 있으면 이 게시물을 이미 다 받은 것(기록 유실·기록 도입 전 구버전)으로 보고 그대로 둔다.
            // 상태 파일이 비었을 때 작가 폴더 전체가 날짜 이름으로 다시 내려오는 것을 막기 위한 판정이다
            if (existing == 0 || existing == names.Count) return new Result(named, renamed);
            // 일부만 있음: 제목이 같은 다른 게시물(기록 도입 전 파일은 소유자를 모름)일 가능성이 커서 다음 후보로.
            // 중간에 끊긴 이 게시물의 재시도일 수도 있지만, 그때 몇 장 중복되는 쪽이 다른 게시물을 잃는 것보다 낫다
        }
        // 마지막 후보는 {id}를 포함해 게시물마다 고유하므로 그대로 쓴다 (이 게시물의 재시도)
        return new Result(last!, renamed);
    }

    /// <summary>템플릿 → 날짜 추가 → 게시물 ID 추가 순의 후보. 템플릿에 이미 들어 있는 항목은 건너뛴다.</summary>
    public static IEnumerable<string> Candidates(string template)
    {
        yield return template;
        var t = template;
        if (!t.Contains("{date}"))
        {
            t = "{date} " + t;
            yield return t;
        }
        if (!t.Contains("{id}"))
        {
            t = t.Contains("{index}") ? t.Replace("{index}", "{id}_{index}") : t + "_{id}";
            yield return t;
        }
    }

    private static List<DownloadItem> Name(IReadOnlyList<DownloadItem> items, string template, string postId, Func<string, string> fill)
    {
        var baseName = fill(template);
        var width = Math.Max(2, items.Count.ToString().Length);
        return items.Select((item, i) =>
        {
            var ext = Path.GetExtension(item.FileName);
            var name = NameSanitizer.Sanitize(baseName.Replace("{index}", (i + 1).ToString().PadLeft(width, '0'))).TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(name)) name = $"{postId}_{i + 1:D2}";
            return item with { FileName = name + ext };
        }).ToList();
    }
}
