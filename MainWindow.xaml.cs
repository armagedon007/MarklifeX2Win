using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using Windows.Devices.Bluetooth.Advertisement;
using System.Diagnostics;

namespace MarklifeWin
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private BluetoothLEDevice? _connectedDevice;
        private GattCharacteristic? _writeCharacteristic;
        private BluetoothLEAdvertisementWatcher? _bleWatcher;

        private NamedPipeServerStream? _pipeServer;
        private Task? _pipeTask;
        private readonly string _pipeName = "marklife-print-pipe";
        //private BluetoothDeviceInfo? _connectedDeviceInfo;
        //private BluetoothClient? _bluetoothClient;;

        private Stream? _stream;

        public ObservableCollection<DeviceItem> Devices { get; } = new();

        private bool _isScanning;
        public bool IsScanning
        {
            get => _isScanning;
            set { _isScanning = value; OnPropertyChanged(); }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            StartPipeServer();
        }

        private void DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }
        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            await ScanAsync();
        }

        private async void GetParamsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string deviceId)
            {
                var device = Devices.FirstOrDefault(d => d.Id == deviceId);
                if (device != null)
                {
                    // Запрашиваем информацию об устройстве
                    await RequestDeviceInfoAsync(device);

                    // Обновляем трей
                    //(App.Current as App)?.UpdateTrayStatus(true, device.Name, device.BatteryLevel);
                }
            }
        }

        public Boolean IsConected()
        {
            var connectedDevice = Devices.FirstOrDefault(d => d.IsConnected ||
                d.IsConnecting);

            return connectedDevice != null;
        }

        public async Task ScanAsync()
        {
            try
            {
                IsScanning = true;
                foreach (var d in Devices)
                {
                    d.IsActive = false;
                }

                // 1. Создаем watcher для рекламных пакетов (рекомендуемый способ для поиска новых устройств)
                _bleWatcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active // Активный режим = получаем Scan Response
                };

                // 2. Обработчик получения данных
                _bleWatcher.Received += (watcher, btAdv) =>
                {
                    // btAdv.Advertisement.LocalName - имя устройства из рекламного пакета
                    // btAdv.BluetoothAddress - адрес устройства
                    // btAdv.RawSignalStrengthInDBm - уровень сигнала

                    // Генерируем временный ID, так как у нас еще нет объекта BluetoothLEDevice
                    string deviceId = btAdv.BluetoothAddress.ToString();
                    if (IsPrinter(btAdv.Advertisement.LocalName))
                    {
                        var device = Devices.FirstOrDefault(d => d.Id == deviceId);
                        if (device != null)
                        {
                            device.IsActive = true;
                            if (device.IsDisconnecting)
                            {
                                device.IsConnected = false;
                                device.Status = "Не подключен";
                                device.ButtonText = "Подключить";
                            }
                        }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrEmpty(btAdv.Advertisement.LocalName) &&
                            !Devices.Any(d => d.Id == deviceId) &&
                            IsPrinter(btAdv.Advertisement.LocalName))
                        {
                            Devices.Add(new DeviceItem
                            {
                                Id = deviceId,
                                Name = btAdv.Advertisement.LocalName,
                                Status = "Не подключено",
                                ButtonText = "Подключить",
                                IsActive = true
                            });
                        }
                    });
                };

                // 3. Запускаем сканирование
                _bleWatcher?.Start();
                // Сканируем 5 секунд
                await Task.Delay(5000);
                foreach (var d in Devices)
                {
                    if (!d.IsActive && !d.IsConnected)
                    {
                        Devices.Remove(d);
                    }
                }
                StopScanning();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка при скане: {ex.Message}");
            }
        }

        private void StopScanning()
        {
            _bleWatcher?.Stop();
            _bleWatcher = null;
            IsScanning = false;
        }

        private bool IsPrinter(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // Расширенный список префиксов для принтеров Marklife
            var prefixes = new[] { "P11", "P12", "P15", "P7", "X2", "S2", "T3", "D1", "L50", "L80", "Marklife", "ML" };
            return prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        private async void DeviceCnnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string deviceId)
            {
                var device = Devices.FirstOrDefault(d => d.Id == deviceId);
                if (device != null)
                {
                    if (device.IsConnected)
                    {
                        await DisconnectDeviceAsync(device);
                    }
                    else
                    {
                        await ConnectDeviceAsync(device);
                    }
                }
            }
        }

        private async Task ConnectDeviceAsync(DeviceItem device)
        {
            try
            {
                device.Status = "Подключение...";
                device.ButtonText = "Подключение...";
                device.IsConnecting = true;

                // Получаем BluetoothLEDevice по адресу
                var address = ulong.Parse(device.Id);
                _connectedDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

                if (_connectedDevice == null)
                {
                    device.Status = "Устройство не найдено";
                    device.ButtonText = "Подключить";
                    return;
                }

                // Получаем все GATT сервисы
                var servicesResult = await _connectedDevice.GetGattServicesAsync();
                if (servicesResult.Status != GattCommunicationStatus.Success)
                {
                    device.Status = "Ошибка получения сервисов";
                    device.ButtonText = "Подключить";
                    _connectedDevice?.Dispose();
                    _connectedDevice = null;
                    return;
                }

                // Ищем сервис принтера (обычно FF00)
                foreach (var service in servicesResult.Services)
                {
                    if (service.Uuid.ToString().ToLower().StartsWith("0000ff00"))
                    {
                        // Получаем характеристики сервиса
                        var characteristicsResult = await service.GetCharacteristicsAsync();
                        if (characteristicsResult.Status == GattCommunicationStatus.Success)
                        {
                            foreach (var characteristic in characteristicsResult.Characteristics)
                            {
                                // Ищем характеристику для записи (обычно FF02)
                                if (characteristic.Uuid.ToString().ToLower().StartsWith("0000ff02"))
                                {
                                    _writeCharacteristic = characteristic;
                                    break;
                                }
                            }
                        }
                        break;
                    }
                }

                if (_writeCharacteristic == null)
                {
                    device.Status = "Сервис печати не найден";
                    device.ButtonText = "Подключить";
                    _connectedDevice?.Dispose();
                    _connectedDevice = null;
                    return;
                }

                device.IsConnected = true;
                device.Status = "Подключен";
                device.ButtonText = "Отключить";
                device.HasParams = Visibility.Visible;
            }
            catch (Exception ex)
            {
                device.Status = $"Ошибка: {ex.Message}";
                device.ButtonText = "Подключить";
                _connectedDevice?.Dispose();
                _connectedDevice = null;
            }
        }

        private async Task DisconnectDeviceAsync(DeviceItem device)
        {
            device.Status = "Отключение...";
            device.ButtonText = "Отключение...";

            // Безопасно отключаем уведомления, если характеристика поддерживает
            if (_writeCharacteristic != null)
            {
                try
                {
                    var properties = _writeCharacteristic.CharacteristicProperties;

                    // Проверяем, поддерживает ли характеристика уведомления
                    if (properties.HasFlag(GattCharacteristicProperties.Notify) ||
                        properties.HasFlag(GattCharacteristicProperties.Indicate))
                    {
                        var status = await _writeCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None);

                        if (status != GattCommunicationStatus.Success)
                        {
                            Debug.WriteLine($"Не удалось отключить уведомления: {status}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Игнорируем ошибки при отключении уведомлений
                    Debug.WriteLine($"Ошибка при отключении уведомлений: {ex.Message}");
                }
                finally
                {
                    _writeCharacteristic = null;
                }
            }

            try
            {
                var services = await _connectedDevice?.GetGattServicesAsync();
                foreach (var service in services.Services)
                {
                    service?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка при освобождении сервисов: {ex.Message}");
            }

            try
            {
                _connectedDevice?.Dispose();
            }
            catch { }
            finally
            {
                _connectedDevice = null;
                _writeCharacteristic = null;

                device.IsDisconnecting = true;
                //device.Status = "Не подключен";
                //device.ButtonText = "Подключить";
                await ScanAsync();
                device.HasParams = Visibility.Collapsed;

                (App.Current as App)?.UpdateTrayStatus(false);
            }
        }

        private async Task RequestDeviceInfoAsync(DeviceItem device)
        {
            if (_writeCharacteristic == null) return;

            try
            {
                // Запрос версии прошивки
                //await SendCommandAsync(new byte[] { 0x10, 0xFF, 0x20, 0xF1 });
                // Запрос серийного номера
                //await SendCommandAsync(new byte[] { 0x10, 0xFF, 0x20, 0xF2 });
                // Запрос уровня батареи
                await SendCommandAsync(new byte[] { 0x10, 0xFF, 0x50, 0xF1 });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка запроса информации: {ex.Message}");
            }
        }

        private async Task SendCommandAsync(byte[] command)
        {
            if (_writeCharacteristic == null) return;

            try
            {
                using var writer = new DataWriter();
                writer.WriteBytes(command);
                await _writeCharacteristic.WriteValueAsync(writer.DetachBuffer());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка отправки команды: {ex.Message}");
            }
        }

        private void StartPipeServer()
        {
            _pipeTask = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        _pipeServer = new NamedPipeServerStream(_pipeName, PipeDirection.InOut);
                        await _pipeServer.WaitForConnectionAsync();

                        using var reader = new StreamReader(_pipeServer);
                        using var writer = new StreamWriter(_pipeServer);

                        string? line = await reader.ReadLineAsync();
                        if (line != null)
                        {
                            await ProcessCommandAsync(line, writer);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Pipe error: {ex.Message}");
                    }
                    finally
                    {
                        _pipeServer?.Dispose();
                    }
                }
            });
        }

        private async Task ProcessCommandAsync(string command, StreamWriter writer)
        {
            if (command.StartsWith("PRINT|"))
            {
                var parts = command.Substring(6).Split('|');
                if (parts.Length >= 7)
                {
                    var filePath = parts[6];
                    await PrintFileAsync(filePath);
                }
                await writer.WriteLineAsync("OK");
            }
            else if (command == "STATUS")
            {
                var status = _connectedDevice != null ? "connected" : "disconnected";
                await writer.WriteLineAsync($"STATUS|{status}");
            }
        }

        private async Task PrintFileAsync(string filePath)
        {
            if (_connectedDevice == null || _writeCharacteristic == null) return;

            var data = await File.ReadAllBytesAsync(filePath);

            try
            {
                // Разбиваем на пакеты по 20 байт
                int chunkSize = 20;
                for (int i = 0; i < data.Length; i += chunkSize)
                {
                    int len = Math.Min(chunkSize, data.Length - i);
                    byte[] chunk = new byte[len];
                    Array.Copy(data, i, chunk, 0, len);

                    using var writer = new DataWriter();
                    writer.WriteBytes(chunk);
                    await _writeCharacteristic.WriteValueAsync(writer.DetachBuffer());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка печати: {ex.Message}");
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Скрываем окно вместо закрытия
            e.Cancel = true;
            Hide();
            base.OnClosing(e);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class DeviceItem : INotifyPropertyChanged
    {
        private string _id = "";
        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        private string _name = "";
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        private string _buttonText = "Подключить";
        public string ButtonText
        {
            get => _buttonText;
            set { _buttonText = value; OnPropertyChanged(); }
        }

        private string _paramsButtonText = "Обновить информацию";
        public string ParamsButtonText
        {
            get => _paramsButtonText;
            set { _paramsButtonText = value; OnPropertyChanged(); }
        }

        private bool _isParamsButton = true;

        public bool IsParamsButton
        {
            get => _isParamsButton;
            set { _isParamsButton = value; OnPropertyChanged(); }
        }

        private Visibility _hasParams = Visibility.Collapsed;
        public Visibility HasParams
        {
            get => _hasParams;
            set { _hasParams = value; OnPropertyChanged(); }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(); }
        }

        private bool _isConnectEnabled = true;

        public bool IsConnectEnabled
        {
            get => _isConnectEnabled;
            set { _isConnectEnabled = value; OnPropertyChanged(); }
        }
        private bool _isDisconnecting;
        public bool IsDisconnecting
        {
            get => _isDisconnecting;
            set { _isDisconnecting = value; IsConnectEnabled = false; OnPropertyChanged(); }
        }

        private bool _isConnecting;
        public bool IsConnecting
        {
            get => _isConnecting;
            set { _isConnecting = value; IsConnectEnabled = false; OnPropertyChanged(); }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; IsConnecting = false; IsDisconnecting = false; IsConnectEnabled = true; OnPropertyChanged(); }
        }

        private string _firmware = "";
        public string Firmware
        {
            get => _firmware;
            set { _firmware = value; OnPropertyChanged(); }
        }

        private int _batteryLevel;
        public int BatteryLevel
        {
            get => _batteryLevel;
            set { _batteryLevel = value; OnPropertyChanged(); Battery = $"Батарея: {value}%"; }
        }

        private string _battery = "-";
        public string Battery
        {
            get => _battery;
            set { _battery = value; OnPropertyChanged(); }
        }

        private string _paper = "-";
        public string Paper
        {
            get => _paper;
            set { _paper = value; OnPropertyChanged(); }
        }

        private string _serial = "-";
        public string Serial
        {
            get => _serial;
            set { _serial = value; OnPropertyChanged(); }
        }

        private string _mac = "-";
        public string Mac
        {
            get => _mac;
            set { _mac = value; OnPropertyChanged(); }
        }

        private string _shutdown = "-";
        public string Shutdown
        {
            get => _shutdown;
            set { _shutdown = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}