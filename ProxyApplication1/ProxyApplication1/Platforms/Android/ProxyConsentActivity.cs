using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;

namespace ProxyApplication1;

[Activity(
    Name = "Company.Hillariot.BarbarisVPN.ProxyConsentActivity",
    Exported = true,               // нужно, чтобы TileService мог её стартовать
    LaunchMode = Android.Content.PM.LaunchMode.SingleTask,
    NoHistory = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar" // чтоб мигала минимально
)]
public class ProxyConsentActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 1) Проверяем, нужно ли согласие
        var prep = VpnService.Prepare(this);
        if (prep != null)
        {
            // 2) Открываем системный диалог
            StartActivityForResult(prep, 100);
        }
        else
        {
            // 3) Согласие уже есть — сразу стартуем сервис и закрываемся
            StartVpnAndFinish();
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        // Если пользователь согласился — запускаем сервис
        if (requestCode == 100 && resultCode == Result.Ok)
        {
            StartVpnAndFinish();
        }
        else
        {
            // Пользователь отменил — просто закрыться
            Finish();
        }
    }

    private void StartVpnAndFinish()
    {
        var start = new Intent(this, typeof(MyProxyService)).SetAction(MyProxyService.ACTION_START);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            StartForegroundService(start);
        else
            StartService(start);

        Finish();
    }
}
