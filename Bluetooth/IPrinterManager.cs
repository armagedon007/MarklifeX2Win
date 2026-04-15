using System;
using System.Threading.Tasks;

namespace MarklifeWin.Bluetooth
{
    public interface IPrinterManager : IDisposable
    {
        bool    IsConnected         { get; }
        string? ConnectedDeviceName { get; }
        bool    AutoReconnect       { get; set; }
        string? LastDeviceId        { get; set; }

        event EventHandler<string>?  DeviceDiscovered;
        event EventHandler<bool>?    ConnectionStateChanged;
        event EventHandler<string>?  StatusChanged;
        event EventHandler<int>?     BatteryLevelChanged;
        event EventHandler<string?>? FirmwareReceived;
        event EventHandler<string?>? SerialReceived;
        event EventHandler<int?>?    PaperLevelReceived;
        event EventHandler<int?>?    ShutdownTimeReceived;
        event EventHandler<string?>? MacAddressReceived;

        Task ScanAsync(int durationMs = 8000);
        Task ConnectAsync(string deviceId);
        Task DisconnectAsync();
        void Disconnect();
        Task SendDataAsync(byte[] data);
        Task RequestBatteryLevelAsync();
        Task RequestAllInfoAsync();
        Task SetShutdownTimeAsync(int minutes);
    }
}
