using System.IO;
using Updater.Core;
using Updater.Providers.Fanbox;
using Updater.Providers.Patreon;

namespace Updater.App;

/// <summary>
/// 프로바이더 레지스트리. 새 사이트를 지원하려면 ISiteProvider 구현을 만들고 여기에 등록한다.
/// 작가 탭·설정 탭의 사이트 전환 버튼과 분류 사이트 콤보는 SupportedProviders로 자동 생성된다.
/// </summary>
public static class ProviderRegistry
{
    public static readonly string[] SupportedProviders = { AppSettings.FanboxProvider, AppSettings.PatreonProvider };

    /// <param name="webViewDataDir">WebView2 프로필 루트 — 사이트별 하위 폴더를 써서 쿠키·캐시가 섞이지 않게 한다</param>
    public static ISiteProvider? Create(SiteSettings site, AppSettings settings, string webViewDataDir)
    {
        if (string.IsNullOrWhiteSpace(site.SessionId)) return null;
        return site.Provider.ToLowerInvariant() switch
        {
            AppSettings.FanboxProvider => new FanboxProvider(site.SessionId, settings.RequestDelayMs, webViewDataDir),
            AppSettings.PatreonProvider => new PatreonProvider(site.SessionId, settings.RequestDelayMs,
                Path.Combine(webViewDataDir, "patreon")),
            _ => null,
        };
    }
}
