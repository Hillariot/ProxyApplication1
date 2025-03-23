using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vpnApplication1
{
    public class VpnService
    {
        public bool IsConnected = false;
        public string StatusMessage = "Выключено";
        public string ButtonText = "Подключиться";
        public string SelectedServer = "Выберите сервер";

        public List<string> Servers = ["США", "Германия", "Франция", "Россия", "Япония"];

        public event Action? OnStatusChanged;


        public void ToggleConnection()
        {
            IsConnected = !IsConnected;
            StatusMessage = IsConnected ? "Подключено" : "Выключено";
            ButtonText = IsConnected ? "Отключиться" : "Подключиться";
        }
    }
}
