using Updater.Core;
using Updater.Providers.Fanbox;

namespace Updater.App;

/// <summary>
/// 프로바이더 레지스트리. 새 사이트를 지원하려면 ISiteProvider 구현을 만들고 여기에 등록한다.
/// </summary>
public static class ProviderRegistry
{
    public static readonly string[] SupportedProviders = { "fanbox" };

    public static ISiteProvider? Create(AccountSettings account, AppSettings settings, string webViewDataDir)
    {
        return account.Provider.ToLowerInvariant() switch
        {
            "fanbox" when !string.IsNullOrWhiteSpace(account.SessionId)
                => new FanboxProvider(account.SessionId, settings.RequestDelayMs, webViewDataDir),
            _ => null,
        };
    }
}
