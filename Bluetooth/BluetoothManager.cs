using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MarklifeWin.Bluetooth
{
    public class BluetoothPrinterManager : IDisposable
    {
        private BluetoothLEDevice? _connectedDevice;
        private GattCharacteristic? _writeCharacteristic;
        private GattCharacteristic? _notifyCharacteristic;
        private Timer? _batteryTimer;
        private bool _disposed;
        private BluetoothLEAdvertisementWatcher? _watcher;
        private readonly HashSet<ulong> _discoveredDevices = new();
        
        public event EventHandler<string>? DeviceDiscovered;
        public event EventHandler<bool>? ConnectionStateChanged;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<int>? BatteryLevelChanged;

        private static readonly Guid PrinterServiceUuid = new Guid("0000FF00-0000-1000-8000-00805F9B34FB");
        private static readonly Guid WriteCharacteristicUuid = new Guid("0000FF02-0000-1000-8000-00805F9B34FB");
        
        private static readonly string[] PrinterPrefixes = new[] { "P11", "P12", "P15", "P7", "X2", "S2", "T3", "D1", "L50", "L80" };

        public bool IsConnected => _connectedDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        public string? ConnectedDeviceName => _connectedDevice?.Name;

        public async Task ScanAsync()
        {
            StatusChanged?.Invoke(this, "Сканирование...");
            _discoveredDevices.Clear();
            
            try
            {
                // Проверяем доступность Bluetooth
                var adapter = await BluetoothAdapter.GetDefaultAsync();
                if (adapter == null)
                {
                    StatusChanged?.Invoke(this, "Bluetooth адаптер не найден");
                    return;
                }

                if (!adapter.IsLowEnergySupported)
                {
                    StatusChanged?.Invoke(this, "Bluetooth LE не поддерживается");
                    return;
                }

                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };
                
                _watcher.Received += OnAdvertisementReceived;
                _watcher.Stopped += OnWatcherStopped;

                _watcher.Start();
                
                Debug.WriteLine("[BT] Сканирование запущено");
                
                // Сканируем 15 секунд
                await Task.Delay(15000);
                
                if (_watcher != null && _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    _watcher.Stop();
                }
                
                if (_discoveredDevices.Count == 0)
                {
                    StatusChanged?.Invoke(this, "Устройства не найдены. Убедитесь что принтер включен.");
                }
                else
                {
                    StatusChanged?.Invoke(this, $"Найдено устройств: {_discoveredDevices.Count}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BT] Ошибка сканирования: {ex}");
                StatusChanged?.Invoke(this, $"Ошибка: {ex.Message}");
            }
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                var name = args.Advertisement.LocalName;
                
                Debug.WriteLine($"[BT] Обнаружено: {name ?? "без имени"} ({args.BluetoothAddress})");
                
                if (!string.IsNullOrEmpty(name) && IsPrinter(name))
                {
                    if (_discoveredDevices.Add(args.BluetoothAddress))
                    {
                        Debug.WriteLine($"[BT] Принтер найден: {name}");
                        DeviceDiscovered?.Invoke(this, $"{name}|{args.BluetoothAddress}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BT] Ошибка обработки рекламы: {ex}");
            }
        }

        private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            Debug.WriteLine($"[BT] Сканирование остановлено. Статус: {args.Error}");
        }

        public async Task ConnectAsync(string deviceId)
        {
            StatusChanged?.Invoke(this, "Подключение...");

            try
            {
                if (ulong.TryParse(deviceId, out ulong address))
                {
                    _connectedDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                }
                else
                {
                    _connectedDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
                }
                
                if (_connectedDevice == null)
                {
                    StatusChanged?.Invoke(this, "Не удалось подключиться");
                    return;
                }

                Debug.WriteLine($"[BT] Подключено к: {_connectedDevice.Name}");

                _connectedDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

                var servicesResult = await _connectedDevice.GetGattServicesAsync();
                if (servicesResult.Status != GattCommunicationStatus.Success)
                {
                    StatusChanged?.Invoke(this, "Не удалось получить сервисы");
                    return;
                }
                
                Debug.WriteLine($"[BT] Найдено сервисов: {servicesResult.Services.Count}");

                foreach (var service in servicesResult.Services)
                {
                    Debug.WriteLine($"[BT] Сервис: {service.Uuid}");
                    
                    if (service.Uuid == PrinterServiceUuid)
                    {
                        Debug.WriteLine("[BT] Найден принтер сервис!");
                        
                        var characteristicsResult = await service.GetCharacteristicsAsync();
                        if (characteristicsResult.Status != GattCommunicationStatus.Success)
                        {
                            continue;
                        }
                        
                        foreach (var characteristic in characteristicsResult.Characteristics)
                        {
                            Debug.WriteLine($"[BT] Характеристика: {characteristic.Uuid} ({characteristic.CharacteristicProperties})");
                            
                            if (characteristic.Uuid == WriteCharacteristicUuid)
                            {
                                _writeCharacteristic = characteristic;
                                Debug.WriteLine("[BT] Найдена write характеристика!");
                            }
                            
                            if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                            {
                                _notifyCharacteristic = characteristic;
                                characteristic.ValueChanged += OnCharacteristicChanged;
                                await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                                Debug.WriteLine("[BT] Подписались на notify!");
                            }
                        }
                    }
                }

                if (_writeCharacteristic == null)
                {
                    StatusChanged?.Invoke(this, "Принтер сервис не найден");
                    return;
                }

                ConnectionStateChanged?.Invoke(this, true);
                StatusChanged?.Invoke(this, $"Подключен к {_connectedDevice.Name}");
                
                // Запускаем периодический опрос батареи
                StartBatteryMonitoring();
                
                // Запрашиваем начальный статус
                await RequestBatteryLevelAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BT] Ошибка подключения: {ex}");
                StatusChanged?.Invoke(this, $"Ошибка: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, false);
            }
        }
        
        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            var isConnected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
            Debug.WriteLine($"[BT] Статус соединения: {isConnected}");
            ConnectionStateChanged?.Invoke(this, isConnected);
            if (!isConnected)
            {
                StatusChanged?.Invoke(this, "Отключен");
            }
        }
        
        private void OnCharacteristicChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);
            
            Debug.WriteLine($"[BT] Получено: {BitConverter.ToString(data)}");
            
            // Парсим ответ батареи: 10 FF 50 <value>
            if (data.Length >= 4 && data[0] == 0x10 && data[1] == 0xFF && data[2] == 0x50)
            {
                int battery = data[3];
                Debug.WriteLine($"[BT] Батарея: {battery}%");
                BatteryLevelChanged?.Invoke(this, battery);
            }
        }
        
        private void StartBatteryMonitoring()
        {
            _batteryTimer?.Dispose();
            _batteryTimer = new Timer(async _ =>
            {
                if (IsConnected)
                {
                    await RequestBatteryLevelAsync();
                }
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        
        public async Task RequestBatteryLevelAsync()
        {
            var command = new byte[] { 0x10, 0xFF, 0x50, 0xF1 };
            await SendCommandAsync(command);
        }
        
        public async Task RequestFirmwareVersionAsync()
        {
            var command = new byte[] { 0x10, 0xFF, 0x20, 0xF1 };
            await SendCommandAsync(command);
        }
        
        public async Task RequestSerialNumberAsync()
        {
            var command = new byte[] { 0x10, 0xFF, 0x20, 0xF2 };
            await SendCommandAsync(command);
        }
        
        public async Task RequestPaperLevelAsync()
        {
            var command = new byte[] { 0x1A, 0x1F, 0x06 };
            await SendCommandAsync(command);
        }
        
        private async Task SendCommandAsync(byte[] command)
        {
            if (_writeCharacteristic == null || _connectedDevice == null) return;
            
            try
            {
                var writer = new DataWriter();
                writer.WriteBytes(command);
                await _writeCharacteristic.WriteValueAsync(writer.DetachBuffer());
                Debug.WriteLine($"[BT] Отправлено: {BitConverter.ToString(command)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BT] Ошибка отправки: {ex}");
                StatusChanged?.Invoke(this, $"Ошибка команды: {ex.Message}");
            }
        }
        
        public void Dispose()
        {
            _disposed = true;
            _watcher?.Stop();
            _watcher = null;
            _batteryTimer?.Dispose();
            _connectedDevice?.Dispose();
        }

        public void Disconnect()
        {
            _watcher?.Stop();
            _connectedDevice?.Dispose();
            _connectedDevice = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            ConnectionStateChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, "Отключен");
        }

        public async Task SendDataAsync(byte[] data)
        {
            if (_writeCharacteristic == null || _connectedDevice == null)
            {
                StatusChanged?.Invoke(this, "Принтер не подключен");
                return;
            }

            try
            {
                int chunkSize = 20;
                for (int i = 0; i < data.Length; i += chunkSize)
                {
                    int len = Math.Min(chunkSize, data.Length - i);
                    byte[] chunk = new byte[len];
                    Array.Copy(data, i, chunk, 0, len);
                    
                    var writer = new DataWriter();
                    writer.WriteBytes(chunk);
                    await _writeCharacteristic.WriteValueAsync(writer.DetachBuffer());
                    
                    await Task.Delay(10);
                }
                
                StatusChanged?.Invoke(this, "Данные отправлены");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Ошибка отправки: {ex.Message}");
            }
        }

        private bool IsPrinter(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            
            foreach (var prefix in PrinterPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}