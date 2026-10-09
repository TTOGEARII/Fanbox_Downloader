using Velopack;

namespace Updater.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 설치/업데이트 훅 — 반드시 앱 시작 최우선으로 실행
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
