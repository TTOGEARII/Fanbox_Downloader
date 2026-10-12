using System.IO;
using Velopack;

namespace Updater.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 설치/업데이트 훅 — 반드시 앱 시작 최우선으로 실행.
        // 바탕화면 바로가기는 만들지 않는다 (패커의 --shortcuts StartMenuRoot와 한 쌍;
        // 구버전에서 올라온 설치본에 남아 있는 바로가기도 설치/업데이트 시점에 지운다).
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => RemoveDesktopShortcut())
            .OnAfterUpdateFastCallback(_ => RemoveDesktopShortcut())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private static void RemoveDesktopShortcut()
    {
        try
        {
            var lnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Fanbox Updater.lnk");
            if (File.Exists(lnk)) File.Delete(lnk);
        }
        catch { /* 바로가기 정리는 실패해도 치명적이지 않음 */ }
    }
}
