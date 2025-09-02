#if ANDROID
using Android.App;
using Android.Content.Res;
using Android.Net;
using Android.OS;
using Java.Lang;
// Алиасы, чтобы избежать конфликтов имён
using JFile = Java.IO.File;               // Java-файл для ProcessBuilder.Directory
using SFile = System.IO.File;             // System.IO.File
using SDirectory = System.IO.Directory;   // System.IO.Directory
using SPath = System.IO.Path;             // System.IO.Path
#endif
using System.Net.Http.Json;
using Console = System.Console;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ProxyApplication1.Services;


namespace ProxyApplication1
{


    public static class AuthTokens
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder.UseMauiApp<App>().ConfigureFonts(fonts => { fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"); });

#if DEBUG 
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug(); // вывод в Output/Logcat
#endif

            builder.Services.AddMauiBlazorWebView();

            builder.Services.AddSingleton<AuthService>();

            builder.Services.AddSingleton<ProxyHelper>();
#if ANDROID
            builder.Services.AddSingleton<MyProxyService>();
                    Task.Run(async () =>
        {
            await CopyAssetToAppData("geosite-category-ads-all.srs");
        });
#endif

#if ANDROID
            builder.Services.AddSingleton<INetworkSpeedService, AndroidNetworkSpeedService>();
#elif WINDOWS
builder.Services.AddSingleton<INetworkSpeedService, WindowsNetworkSpeedService>();
#else
builder.Services.AddSingleton<INetworkSpeedService, NoopSpeedService>();
#endif

            builder.Services.AddSingleton<InAppBrowserService>();

            builder.Services.AddAuthorizationCore();

            builder.Services.AddScoped<CustomAuthStateProvider>();
            builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());

            // токены
            builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
            builder.Services.AddSingleton<IAuthService, AuthService>();

            // Фабрика HttpClient + именованные клиенты
            builder.Services.AddHttpClient(); // <- базовая регистрация

            builder.Services.AddHttpClient("Auth", c =>
            {
                c.BaseAddress = new System.Uri("http://185.184.122.25:5000/"); // <-- ВАШ БАЗОВЫЙ АДРЕС (http, 5000)
            });

            builder.Services.AddTransient<AuthHttpMessageHandler>();
            builder.Services.AddHttpClient("Api", c =>
            {
                c.BaseAddress = new System.Uri("http://185.184.122.25:5000/"); // если остальной API там же
            })
            .AddHttpMessageHandler<AuthHttpMessageHandler>();





            return builder.Build();
        }
        private static async Task CopyAssetToAppData(string fileName)
        {
            using var src = await FileSystem.OpenAppPackageFileAsync(fileName);
            var dstPath = Path.Combine(FileSystem.AppDataDirectory, fileName);
            if (!File.Exists(dstPath))
            {
                using var dst = File.Create(dstPath);
                await src.CopyToAsync(dst);
            }
        }
    }

    public class InAppBrowserService
    {
        public Task OpenAsync(string url) =>
            MainThread.InvokeOnMainThreadAsync(() =>
                MainPage.Instance?.ShowBrowser(url));

        public Task CloseAsync() =>
            MainThread.InvokeOnMainThreadAsync(() =>
                MainPage.Instance?.HideBrowser());
    }

}
