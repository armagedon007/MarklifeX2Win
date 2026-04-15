using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using MarklifeWin.Bluetooth;

namespace MarklifeWin
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly IPrinterManager _bt;
        private bool _isScanning;

        public ObservableCollection<DeviceItem> Devices { get; } = new();

        public bool IsScanning
        {
            get => _isScanning;
            set { _isScanning = value; OnPropertyChanged(); }
        }

        public MainWindow(IPrinterManager bt)
        {
            _bt = bt;
            InitializeComponent();
            DataContext = this;
            WireEvents();
        }

        private void WireEvents()
        {
            _bt.DeviceDiscovered       += OnDeviceDiscovered;
            _bt.ConnectionStateChanged += OnConnectionStateChanged;
            _bt.StatusChanged          += OnStatusChanged;
            _bt.BatteryLevelChanged    += OnBatteryLevelChanged;
            _bt.FirmwareReceived       += (_, v) => UpdateDevice(d => d.Firmware = v ?? "-");
            _bt.SerialReceived         += (_, v) => UpdateDevice(d => d.Serial   = v ?? "-");
            _bt.PaperLevelReceived     += (_, v) => UpdateDevice(d => d.Paper    = v.HasValue ? $"{v}%" : "-");
            _bt.ShutdownTimeReceived   += OnShutdownTimeReceived;
            _bt.MacAddressReceived     += (_, v) => UpdateDevice(d => d.Mac      = v ?? "-");
        }

        // ── Event handlers ───────────────────────────────────────────────────

        private void OnDeviceDiscovered(object? sender, string info)
        {
            var parts = info.Split('|');
            if (parts.Length < 2) return;
            var name = parts[0];
            var id   = parts[1];

            Dispatcher.Invoke(() =>
            {
                var existing = Devices.FirstOrDefault(d => d.Id == id);
                if (existing != null)
                {
                    existing.IsActive = true;
                    return;
                }

                // Восстанавливаем AutoReconnect из настроек для этого устройства
                var s = MarklifeWin.Properties.Settings.Default;
                bool autoReconnect = s.AutoReconnect && s.LastDeviceAddress == id;

                Devices.Add(new DeviceItem
                {
                    Id            = id,
                    Name          = name,
                    Status        = "Не подключен",
                    ButtonText    = "Подключить",
                    IsActive      = true,
                    AutoReconnect = autoReconnect
                });
            });
        }

        private void OnConnectionStateChanged(object? sender, bool connected)
        {
            Dispatcher.Invoke(() =>
            {
                var device = Devices.FirstOrDefault(d => d.IsConnected || d.IsConnecting);
                if (device == null) device = Devices.FirstOrDefault(d => d.Id == _bt.LastDeviceId);

                // Если устройство не в списке (автоподключение без сканирования) — добавляем
                if (device == null && connected && _bt.LastDeviceId != null)
                {
                    device = new DeviceItem
                    {
                        Id         = _bt.LastDeviceId,
                        Name       = _bt.ConnectedDeviceName ?? "Marklife X2",
                        Status     = "Подключен",
                        ButtonText = "Отключить",
                        IsActive   = true
                    };
                    Devices.Add(device);
                }

                if (connected)
                {
                    if (device != null)
                    {
                        device.IsConnected = true;
                        device.Status      = "Подключен";
                        device.ButtonText  = "Отключить";
                        device.HasParams   = Visibility.Visible;
                    }
                    (App.Current as App)?.UpdateTrayStatus(true, _bt.ConnectedDeviceName ?? "", 0);
                }
                else
                {
                    if (device != null)
                    {
                        device.IsConnected = false;
                        device.Status      = "Не подключен";
                        device.ButtonText  = "Подключить";
                        device.HasParams   = Visibility.Collapsed;
                    }
                    (App.Current as App)?.UpdateTrayStatus(false);
                }
            });
        }

        private void OnStatusChanged(object? sender, string status)
        {
            Debug.WriteLine($"[UI] Status: {status}");
        }

        private void OnBatteryLevelChanged(object? sender, int level)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateDevice(d => d.BatteryLevel = level);
                (App.Current as App)?.UpdateTrayBattery(level);
            });
        }

        private void OnShutdownTimeReceived(object? sender, int? minutes)
        {
            Dispatcher.Invoke(() =>
            {
                var device = Devices.FirstOrDefault(d => d.IsConnected);
                if (device == null) return;
                device.ShutdownMinutes = minutes ?? 0;
            });
        }

        private void UpdateDevice(Action<DeviceItem> action)
        {
            Dispatcher.Invoke(() =>
            {
                var device = Devices.FirstOrDefault(d => d.IsConnected);
                if (device != null) action(device);
            });
        }

        // ── UI Handlers ──────────────────────────────────────────────────────

        private void DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CloseWindow(object sender, RoutedEventArgs e) => Hide();

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (IsScanning) return;
            await RunScanAsync();
        }

        public async Task RunScanAsync()
        {
            IsScanning = true;
            // Помечаем неподключённые как неактивные — если не найдутся, удалим
            foreach (var d in Devices.Where(d => !d.IsConnected && d.IsConnectEnabled).ToList())
                d.IsActive = false;

            await _bt.ScanAsync(8000);

            // Удаляем только те что не нашлись, не подключены и кнопка не заблокирована
            foreach (var d in Devices.Where(d => !d.IsActive && !d.IsConnected && d.IsConnectEnabled).ToList())
                Devices.Remove(d);

            IsScanning = false;
        }

        private async void DeviceCnnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var device = Devices.FirstOrDefault(d => d.Id == id);
                if (device == null) return;

                if (device.IsConnected)
                    await DisconnectAsync(device);
                else
                    await ConnectAsync(device);
            }
        }

        private async Task ConnectAsync(DeviceItem device)
        {
            device.Status     = "Подключение...";
            device.ButtonText = "Подключение...";
            device.IsConnecting = true;
            await _bt.ConnectAsync(device.Id);
        }

        private async Task DisconnectAsync(DeviceItem device)
        {
            // Блокируем кнопку на время отключения
            device.IsConnectEnabled = false;
            device.Status     = "Отключение...";
            device.ButtonText = "Отключение...";

            await _bt.DisconnectAsync();

            // Сбрасываем состояние, но НЕ удаляем из списка
            device.IsConnected = false;
            device.HasParams   = Visibility.Collapsed;
            device.Status      = "Не подключен";
            device.ButtonText  = "Подключить";
            device.IsActive    = true; // не удалять при следующем скане
            device.ShutdownMinutes = -1; // сброс — при следующем подключении снова прочерк

            // Ждём пока принтер снова появится в эфире, потом разблокируем
            _ = Task.Run(async () =>
            {
                await _bt.ScanAsync(10000);
                await Dispatcher.InvokeAsync(() =>
                {
                    // Если устройство всё ещё в списке — разблокируем кнопку
                    var d = Devices.FirstOrDefault(x => x.Id == device.Id);
                    if (d != null)
                    {
                        d.IsConnectEnabled = true;
                        d.IsActive = true;
                    }
                });
            });
        }

        private async void GetParamsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var device = Devices.FirstOrDefault(d => d.Id == id);
                if (device == null || !device.IsConnected) return;
                device.IsParamsButton = false;
                device.ParamsButtonText = "Загрузка...";
                await _bt.RequestAllInfoAsync();
                device.IsParamsButton = true;
                device.ParamsButtonText = "Обновить";
            }
        }

        private async void ShutdownCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo) return;
            if (combo.Tag is not DeviceItem device) return;
            if (!device.IsConnected) return;

            int[] minuteValues = { 5, 10, 15, 30, 60 };
            int idx = combo.SelectedIndex;
            if (idx < 0 || idx >= minuteValues.Length) return;

            int minutes = minuteValues[idx];
            if (device.ShutdownMinutes == minutes) return;

            // Блокируем кнопку и комбобокс
            device.IsParamsButton = false;
            device.ParamsButtonText = "Применение...";
            try
            {
                await _bt.SetShutdownTimeAsync(minutes);
                device.ShutdownMinutes = minutes;
            }
            finally
            {
                device.IsParamsButton = true;
                device.ParamsButtonText = "Обновить";
            }
        }

        private void AutoReconnectToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.Tag is DeviceItem device)
            {
                _bt.AutoReconnect = device.AutoReconnect;
                _bt.LastDeviceId  = device.AutoReconnect ? device.Id : null;

                // Сохраняем в настройки
                var s = MarklifeWin.Properties.Settings.Default;
                s.AutoReconnect      = device.AutoReconnect;
                s.LastDeviceAddress  = device.AutoReconnect ? device.Id : "";
                s.Save();
                Debug.WriteLine($"[UI] AutoReconnect saved: {device.AutoReconnect}, id={device.Id}");
            }
        }

        public bool IsConnected() => Devices.Any(d => d.IsConnected || d.IsConnecting);

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════
    // DeviceItem
    // ════════════════════════════════════════════════════════════════════════
    public class DeviceItem : INotifyPropertyChanged
    {
        private string _id = "";
        public string Id { get => _id; set { _id = value; OnPropertyChanged(); } }

        private string _name = "";
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

        private string _status = "";
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

        private string _buttonText = "Подключить";
        public string ButtonText { get => _buttonText; set { _buttonText = value; OnPropertyChanged(); } }

        private string _paramsButtonText = "Обновить";
        public string ParamsButtonText { get => _paramsButtonText; set { _paramsButtonText = value; OnPropertyChanged(); } }

        private bool _isParamsButton = true;
        public bool IsParamsButton { get => _isParamsButton; set { _isParamsButton = value; OnPropertyChanged(); } }

        private Visibility _hasParams = Visibility.Collapsed;
        public Visibility HasParams { get => _hasParams; set { _hasParams = value; OnPropertyChanged(); } }

        private bool _isActive;
        public bool IsActive { get => _isActive; set { _isActive = value; OnPropertyChanged(); } }

        private bool _isConnectEnabled = true;
        public bool IsConnectEnabled { get => _isConnectEnabled; set { _isConnectEnabled = value; OnPropertyChanged(); } }

        private bool _isConnecting;
        public bool IsConnecting
        {
            get => _isConnecting;
            set { _isConnecting = value; IsConnectEnabled = !value; OnPropertyChanged(); }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; IsConnecting = false; IsConnectEnabled = true; OnPropertyChanged(); }
        }

        private int _batteryLevel;
        public int BatteryLevel
        {
            get => _batteryLevel;
            set { _batteryLevel = value; Battery = $"{value}%"; OnPropertyChanged(); }
        }

        private string _battery = "-";
        public string Battery { get => _battery; set { _battery = value; OnPropertyChanged(); } }

        private string _paper = "-";
        public string Paper { get => _paper; set { _paper = value; OnPropertyChanged(); } }

        private string _serial = "-";
        public string Serial { get => _serial; set { _serial = value; OnPropertyChanged(); } }

        private string _mac = "-";
        public string Mac { get => _mac; set { _mac = value; OnPropertyChanged(); } }

        private string _firmware = "-";
        public string Firmware { get => _firmware; set { _firmware = value; OnPropertyChanged(); } }

        // Shutdown time in minutes; -1 = unknown (show dash)
        private int _shutdownMinutes = -1;
        public int ShutdownMinutes
        {
            get => _shutdownMinutes;
            set
            {
                _shutdownMinutes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShutdownSelectedIndex));
                OnPropertyChanged(nameof(ShutdownKnown));
                OnPropertyChanged(nameof(ShutdownUnknown));
                OnPropertyChanged(nameof(ShutdownDisplayText));
            }
        }

        // true когда значение получено от принтера
        public bool ShutdownKnown   => _shutdownMinutes > 0;
        public bool ShutdownUnknown => _shutdownMinutes <= 0;

        // Maps minutes -> ComboBox index: 0=5min,1=10,2=15,3=30,4=60
        // Returns -1 if value not in list (will show raw value as text instead)
        public int ShutdownSelectedIndex
        {
            get => _shutdownMinutes switch { 5 => 0, 10 => 1, 15 => 2, 30 => 3, 60 => 4, _ => -1 };
        }

        // Текст для отображения если значение не в списке
        public string ShutdownDisplayText => _shutdownMinutes > 0 ? $"{_shutdownMinutes} мин" : "—";

        private bool _autoReconnect;
        public bool AutoReconnect { get => _autoReconnect; set { _autoReconnect = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
