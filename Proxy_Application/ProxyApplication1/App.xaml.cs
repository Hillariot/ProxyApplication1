
namespace ProxyApplication1;

public partial class App : Application
{
    public App(IAuthService auth)
    {
        InitializeComponent();
        MainPage = new MainPage();

        // Асинхронную инициализацию можно не блокировать UI
        _ = auth.InitializeAsync();

    }
}
