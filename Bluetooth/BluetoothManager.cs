using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MarklifeWin.Bluetooth
{
    public class BluetoothPrinterManager : IPrinterManager
    {
        // ── Характеристики ──────────────────────────────────────────────────
        private static readonly Guid ServiceUuid          = new("0000FF00-0000-1000-8000-00805F9B34FB");
        private static readonly Guid WriteCharUuid        = new("0000FF02-0000-1000-8000-00805F9B34FB");
        private static readonly Guid NotifyCharUuid       = new("0000FF01-0000-1000-8000-00805F9B34FB");
        private static readonly Guid FlowControlCharUuid  = new("0000FF03-0000-1000-8000-00805F9B34FB");

        private static readonly string[] PrinterPrefixes = {
            "X2"
        };

        // ── Состояние ────────────────────────────────────────────────────────
        private BluetoothLEDevice?      _device;
        private GattCharacteristic?     _writeChar;
        private GattCharacteristic?     _notifyChar;
        private BluetoothLEAdvertisementWatcher? _watcher;

        // Все подписанные notify-характеристики — для отписки при disconnect
        private readonly List<GattCharacteristic> _subscribedChars = new();

        // Flow control (как в BLE.swift)
        private int  _availableCredits = 4;
        private int  _mtuSize          = 20;
        private readonly object _creditLock = new();

        // Read callback
        private Action<byte[]?>? _readCallback;
        private readonly List<byte> _receiveBuffer = new();
        private readonly object _bufferLock = new();

        // Battery polling
        private Timer? _batteryTimer;
        // Auto-reconnect
        private Timer? _reconnectTimer;
        private string? _lastDeviceId;
        private bool    _autoReconnect;
        private bool    _disposed;

        // ── События ──────────────────────────────────────────────────────────
        public event EventHandler<string>?  DeviceDiscovered;       // "Name|Address"
        public event EventHandler<bool>?    ConnectionStateChanged; // true=connected
        public event EventHandler<string>?  StatusChanged;
        public event EventHandler<int>?     BatteryLevelChanged;
        public event EventHandler<string?>? FirmwareReceived;
        public event EventHandler<string?>? SerialReceived;
        public event EventHandler<int?>?    PaperLevelReceived;
        public event EventHandler<int?>?    ShutdownTimeReceived;
        public event EventHandler<string?>? MacAddressReceived;

        // ── Публичные свойства ───────────────────────────────────────────────
        public bool    IsConnected        => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected && _writeChar != null;
        public string? ConnectedDeviceName => _device?.Name;

        public bool AutoReconnect
        {
            get => _autoReconnect;
            set
            {
                _autoReconnect = value;
                if (value) StartReconnectTimer();
                else       StopReconnectTimer();
            }
        }

        public string? LastDeviceId
        {
            get => _lastDeviceId;
            set => _lastDeviceId = value;
        }

        // ════════════════════════════════════════════════════════════════════
        // SCAN
        // ════════════════════════════════════════════════════════════════════
        public async Task ScanAsync(int durationMs = 10000)
        {
            StatusChanged?.Invoke(this, "Сканирование...");

            try
            {
                var adapter = await BluetoothAdapter.GetDefaultAsync();
                if (adapter == null || !adapter.IsLowEnergySupported)
                {
                    StatusChanged?.Invoke(this, "Bluetooth LE не поддерживается");
                    return;
                }

                _watcher?.Stop();
                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };
                _watcher.Received += OnAdvertisementReceived;
                _watcher.Start();

                await Task.Delay(durationMs);

                if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                    _watcher.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BT] Scan error: {ex.Message}");
                StatusChanged?.Invoke(this, $"Ошибка сканирования: {ex.Message}");
            }
        }

        private readonly HashSet<ulong> _seen = new();

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender,
                                              BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName;
            if (string.IsNullOrEmpty(name) || !IsPrinter(name)) return;
            if (!_seen.Add(args.BluetoothAddress)) return;

            Debug.WriteLine($"[BT] Found: {name} ({args.BluetoothAddress})");
            DeviceDiscovered?.Invoke(this, $"{name}|{args.BluetoothAddress}");
        }

        public void StopScan()
        {
            _watcher?.Stop();
            _watcher = null;
        }

        // ════════════════════════════════════════════════════════════════════
        // CONNECT
        // ════════════════════════════════════════════════════════════════════
        public async Task ConnectAsync(string deviceId)
        {
            StatusChanged?.Invoke(this, "Подключение...");
            try
            {
                if (!ulong.TryParse(deviceId, out ulong address))
                {
                    StatusChanged?.Invoke(this, "Неверный адрес устройства");
                    ConnectionStateChanged?.Invoke(this, false);
                    return;
                }

                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (_device == null)
                {
                    StatusChanged?.Invoke(this, "Устройство не найдено");
                    ConnectionStateChanged?.Invoke(this, false);
                    return;
                }

                _device.ConnectionStatusChanged += OnConnectionStatusChanged;

                // Discover services
                var svcResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                if (svcResult.Status != GattCommunicationStatus.Success)
                {
                    StatusChanged?.Invoke(this, "Ошибка получения сервисов");
                    ConnectionStateChanged?.Invoke(this, false);
                    return;
                }

                // Standard BLE system services to skip (Generic Access, Generic Attribute, etc.)
                var systemServices = new HashSet<Guid>
                {
                    new("00001800-0000-1000-8000-00805f9b34fb"), // Generic Access
                    new("00001801-0000-1000-8000-00805f9b34fb"), // Generic Attribute
                    new("0000180a-0000-1000-8000-00805f9b34fb"), // Device Information
                };

                foreach (var svc in svcResult.Services)
                {
                    Debug.WriteLine($"[BT] Service: {svc.Uuid}");

                    // Skip system services — subscribing to their characteristics throws UnauthorizedAccessException
                    if (systemServices.Contains(svc.Uuid))
                    {
                        Debug.WriteLine($"[BT] Skipping system service: {svc.Uuid}");
                        continue;
                    }

                    var charResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (charResult.Status != GattCommunicationStatus.Success) continue;

                    foreach (var ch in charResult.Characteristics)
                    {
                        Debug.WriteLine($"[BT] Char: {ch.Uuid} props={ch.CharacteristicProperties}");

                        if (ch.Uuid == WriteCharUuid)
                        {
                            _writeChar = ch;
                            Debug.WriteLine("[BT] Write char found (FF02)");
                        }

                        // Subscribe to notify/indicate
                        bool hasNotify  = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify);
                        bool hasIndicate = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate);
                        if (hasNotify || hasIndicate)
                        {
                            try
                            {
                                var cccdValue = hasNotify
                                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                                    : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

                                var writeStatus = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);
                                if (writeStatus == GattCommunicationStatus.Success)
                                {
                                    ch.ValueChanged += OnCharacteristicValueChanged;
                                    _subscribedChars.Add(ch);
                                    Debug.WriteLine($"[BT] Subscribed to {(hasNotify ? "notify" : "indicate")}: {ch.Uuid}");
                                }
                                else
                                {
                                    Debug.WriteLine($"[BT] Subscribe failed ({writeStatus}): {ch.Uuid}");
                                }

                                if (ch.Uuid == NotifyCharUuid)
                                    _notifyChar = ch;
                            }
                            catch (UnauthorizedAccessException)
                            {
                                Debug.WriteLine($"[BT] Skipping protected characteristic: {ch.Uuid}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[BT] Subscribe error for {ch.Uuid}: {ex.Message}");
                            }
                        }
                    }
                }

                if (_writeChar == null)
                {
                    StatusChanged?.Invoke(this, "Сервис принтера не найден");
                    ConnectionStateChanged?.Invoke(this, false);
                    return;
                }

                _lastDeviceId = deviceId;
                ConnectionStateChanged?.Invoke(this, true);
                StatusChanged?.Invoke(this, $"Подключен к {_device.Name}");

                // Initial battery request + start polling
                await Task.Delay(500);
                await RequestBatteryLevelAsync();
                StartBatteryTimer();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BT] Connect error: {ex}");
                StatusChanged?.Invoke(this, $"Ошибка: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, false);
            }
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
            Debug.WriteLine($"[BT] Connection status: {connected}");
            ConnectionStateChanged?.Invoke(this, connected);
            if (!connected)
            {
                StatusChanged?.Invoke(this, "Принтер отключился");
                StopBatteryTimer();
                // Try auto-reconnect
                if (_autoReconnect && _lastDeviceId != null)
                    StartReconnectTimer();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // DISCONNECT
        // ════════════════════════════════════════════════════════════════════
        public async Task DisconnectAsync()
        {
            StopBatteryTimer();
            StopReconnectTimer();

            // 1. Отписываемся от notify/indicate — пишем None в CCCD
            foreach (var ch in _subscribedChars)
            {
                try
                {
                    ch.ValueChanged -= OnCharacteristicValueChanged;
                    var props = ch.CharacteristicProperties;
                    if (props.HasFlag(GattCharacteristicProperties.Notify) ||
                        props.HasFlag(GattCharacteristicProperties.Indicate))
                    {
                        await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None);
                        Debug.WriteLine($"[BT] Unsubscribed CCCD: {ch.Uuid}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BT] Unsubscribe error {ch.Uuid}: {ex.Message}");
                }
            }
            _subscribedChars.Clear();

            // 2. Освобождаем GATT сервисы явно (как в оригинале)
            if (_device != null)
            {
                try
                {
                    var services = await _device.GetGattServicesAsync();
                    foreach (var svc in services.Services)
                    {
                        try { svc.Dispose(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BT] Service dispose error: {ex.Message}");
                }
            }

            // 3. Очищаем устройство
            CleanupDevice();
            ConnectionStateChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, "Отключен");
        }

        public void Disconnect() => _ = DisconnectAsync();

        private void CleanupDevice()
        {
            if (_device != null)
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

            _writeChar  = null;
            _notifyChar = null;
            lock (_bufferLock) _receiveBuffer.Clear();
            _readCallback = null;

            try { _device?.Dispose(); } catch { }
            _device = null;
        }

        // ════════════════════════════════════════════════════════════════════
        // RECEIVE DATA
        // ════════════════════════════════════════════════════════════════════
        private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var data   = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            Debug.WriteLine($"[BT] Received from {sender.Uuid}: {BitConverter.ToString(data)}");

            // Flow control (FF03)
            if (sender.Uuid == FlowControlCharUuid)
            {
                HandleFlowControl(data);
                return;
            }

            // Accumulate in buffer
            lock (_bufferLock)
                _receiveBuffer.AddRange(data);

            // Fire callback only if we have a complete response
            var cb = _readCallback;
            if (cb != null)
            {
                byte[] buf;
                lock (_bufferLock) buf = _receiveBuffer.ToArray();

                // For paper level (1A 1F 06): need at least 8 bytes
                bool isPaperQuery = buf.Length >= 3 && buf[0] == 0x1A && buf[1] == 0x1F && buf[2] == 0x06;
                if (isPaperQuery && buf.Length < 8)
                {
                    Debug.WriteLine($"[BT] Paper response incomplete: {buf.Length}/8 bytes, waiting...");
                    return; // wait for more packets
                }

                // For all other responses — fire immediately
                if (Interlocked.Exchange(ref _readCallback, null) != null)
                    cb(buf);
                return;
            }

            // Parse unsolicited responses
            ParseResponse(data);
        }

        private void HandleFlowControl(byte[] data)
        {
            lock (_creditLock)
            {
                if (data.Length == 2 && data[0] == 0x01)
                {
                    int credits = data[1];
                    _availableCredits = credits == 0x04 ? 4 : _availableCredits + credits;
                    Debug.WriteLine($"[BT] Flow control credits: {_availableCredits}");
                }
                else if (data.Length == 3 && data[0] == 0x02)
                {
                    int mtu = data[1] | (data[2] << 8);
                    _mtuSize = Math.Max(20, mtu - 3);
                    Debug.WriteLine($"[BT] Flow control MTU: {_mtuSize}");
                }
            }
        }

        private void ParseResponse(byte[] data)
        {
            if (data.Length == 0) return;

            // Battery: 10 FF 50 F1 -> response: 00 XX
            if (data.Length >= 2 && data[0] == 0x00)
            {
                int battery = data[1];
                if (battery >= 0 && battery <= 100)
                {
                    Debug.WriteLine($"[BT] Battery: {battery}%");
                    BatteryLevelChanged?.Invoke(this, battery);
                    return;
                }
            }

            // Shutdown time: single byte — НЕ парсим здесь, слишком много ложных срабатываний.
            // Парсинг только в RequestShutdownTimeAsync по полному ответу.

            // Paper level: 1A 1F 06 XX ...
            if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x1F && data[2] == 0x06)
            {
                int paper = data[3] * 2;
                Debug.WriteLine($"[BT] Paper: {paper}%");
                PaperLevelReceived?.Invoke(this, paper);
                return;
            }

            // Firmware: ASCII starting with 'V'
            var str = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0', '\r', '\n');
            if (str.StartsWith("V") && str.Contains("."))
            {
                Debug.WriteLine($"[BT] Firmware: {str}");
                FirmwareReceived?.Invoke(this, str);
                return;
            }

            // Serial: ASCII starting with device prefix
            if (str.Length > 2 && (str.StartsWith("X2") || str.StartsWith("P") || str.StartsWith("S")))
            {
                Debug.WriteLine($"[BT] Serial: {str}");
                SerialReceived?.Invoke(this, str);
                return;
            }

            // MAC: 6 bytes
            if (data.Length == 6)
            {
                var mac = BitConverter.ToString(data).Replace("-", ":");
                Debug.WriteLine($"[BT] MAC: {mac}");
                MacAddressReceived?.Invoke(this, mac);
                return;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // SEND COMMAND + READ RESPONSE
        // ════════════════════════════════════════════════════════════════════
        private async Task<byte[]?> SendCommandAndReadAsync(byte[] command, int timeoutMs = 3000)
        {
            if (_writeChar == null) return null;

            lock (_bufferLock) _receiveBuffer.Clear();

            var tcs = new TaskCompletionSource<byte[]?>();
            using var cts = new CancellationTokenSource(timeoutMs);

            _readCallback = data => tcs.TrySetResult(data);
            cts.Token.Register(() => tcs.TrySetResult(null));

            try
            {
                var writer = new DataWriter();
                writer.WriteBytes(command);
                await _writeChar.WriteValueAsync(writer.DetachBuffer());
                Debug.WriteLine($"[BT] Sent: {BitConverter.ToString(command)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BT] Send error: {ex.Message}");
                _readCallback = null;
                return null;
            }

            return await tcs.Task;
        }

        // ════════════════════════════════════════════════════════════════════
        // PRINTER COMMANDS
        // ════════════════════════════════════════════════════════════════════
        public async Task RequestBatteryLevelAsync()
        {
            var data = await SendCommandAndReadAsync(new byte[] { 0x10, 0xFF, 0x50, 0xF1 }, 3000);
            if (data != null && data.Length >= 2)
            {
                int battery = data[1];
                Debug.WriteLine($"[BT] Battery response: {battery}%");
                BatteryLevelChanged?.Invoke(this, battery);
            }
        }

        public async Task RequestFirmwareVersionAsync()
        {
            var data = await SendCommandAndReadAsync(new byte[] { 0x10, 0xFF, 0x20, 0xF1 }, 3000);
            if (data != null && data.Length > 0)
            {
                var ver = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0', '\r', '\n');
                FirmwareReceived?.Invoke(this, ver);
            }
        }

        public async Task RequestSerialNumberAsync()
        {
            var data = await SendCommandAndReadAsync(new byte[] { 0x10, 0xFF, 0x20, 0xF2 }, 3000);
            if (data != null && data.Length > 0)
            {
                int end = Array.IndexOf(data, (byte)0);
                var serial = System.Text.Encoding.ASCII.GetString(data, 0, end < 0 ? data.Length : end);
                SerialReceived?.Invoke(this, serial);
            }
        }

        public async Task RequestPaperLevelAsync()
        {
            // Ответ минимум 8 байт, начинается с 1A 1F 06, байт [3] * 2 = процент (как в BLE.swift)
            var data = await SendCommandAndReadAsync(new byte[] { 0x1A, 0x1F, 0x06 }, 20000);
            if (data == null)
            {
                Debug.WriteLine("[BT] Paper level: no response");
                return;
            }

            Debug.WriteLine($"[BT] Paper raw: {BitConverter.ToString(data)} ({data.Length} bytes)");

            if (data.Length >= 8 && data[0] == 0x1A && data[1] == 0x1F && data[2] == 0x06)
            {
                int paper = data[3] * 2;
                Debug.WriteLine($"[BT] Paper level: {paper}%");
                PaperLevelReceived?.Invoke(this, paper);
            }
            else
            {
                Debug.WriteLine($"[BT] Paper level: unexpected format, need >= 8 bytes starting with 1A 1F 06");
            }
        }

        public async Task RequestMacAddressAsync()
        {
            var data = await SendCommandAndReadAsync(new byte[] { 0x10, 0xFF, 0x20, 0xF3 }, 10000);
            if (data != null && data.Length >= 6)
            {
                var mac = BitConverter.ToString(data, 0, 6).Replace("-", ":");
                MacAddressReceived?.Invoke(this, mac);
            }
        }

        public async Task RequestShutdownTimeAsync()
        {
            // Ответ: 1 байт = минуты (data[0]), таймаут 10 сек как в BLE.swift
            var data = await SendCommandAndReadAsync(new byte[] { 0x10, 0xFF, 0x13 }, 10000);
            if (data == null || data.Length == 0) return;

            Debug.WriteLine($"[BT] Shutdown raw: {BitConverter.ToString(data)} ({data.Length} bytes)");

            int minutes = data[0];
            Debug.WriteLine($"[BT] Shutdown time: {minutes} min");

            if (minutes > 0)
                ShutdownTimeReceived?.Invoke(this, minutes);
        }

        /// <summary>Установить время автоотключения (минуты). Команда: 10 FF 12 HH LL</summary>
        public async Task SetShutdownTimeAsync(int minutes)
        {
            byte hi = (byte)(minutes / 256);
            byte lo = (byte)(minutes % 256);
            await SendRawAsync(new byte[] { 0x10, 0xFF, 0x12, hi, lo });
            Debug.WriteLine($"[BT] Set shutdown time: {minutes} min");
        }

        public async Task RequestAllInfoAsync()
        {
            await RequestBatteryLevelAsync();
            await Task.Delay(500);
            await RequestFirmwareVersionAsync();
            await Task.Delay(500);
            await RequestSerialNumberAsync();
            await Task.Delay(500);
            await RequestPaperLevelAsync();
            await Task.Delay(500);
            await RequestShutdownTimeAsync();
            await Task.Delay(500);
            await RequestMacAddressAsync();
        }

        // ════════════════════════════════════════════════════════════════════
        // SEND DATA (print)
        // ════════════════════════════════════════════════════════════════════
        public async Task SendDataAsync(byte[] data)
        {
            if (_writeChar == null)
            {
                StatusChanged?.Invoke(this, "Принтер не подключен");
                return;
            }

            try
            {
                int chunkSize = Math.Max(20, _mtuSize);
                for (int i = 0; i < data.Length; i += chunkSize)
                {
                    // Wait for credits
                    for (int attempt = 0; attempt < 100; attempt++)
                    {
                        lock (_creditLock)
                        {
                            if (_availableCredits > 0) { _availableCredits--; break; }
                        }
                        await Task.Delay(100);
                    }

                    int len   = Math.Min(chunkSize, data.Length - i);
                    var chunk = new byte[len];
                    Array.Copy(data, i, chunk, 0, len);

                    var writer = new DataWriter();
                    writer.WriteBytes(chunk);
                    await _writeChar.WriteValueAsync(writer.DetachBuffer());
                    await Task.Delay(10);
                }
                StatusChanged?.Invoke(this, "Данные отправлены");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Ошибка отправки: {ex.Message}");
            }
        }

        private async Task SendRawAsync(byte[] command)
        {
            if (_writeChar == null) return;
            try
            {
                var writer = new DataWriter();
                writer.WriteBytes(command);
                await _writeChar.WriteValueAsync(writer.DetachBuffer());
                Debug.WriteLine($"[BT] Raw sent: {BitConverter.ToString(command)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BT] Raw send error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // BATTERY TIMER (every 5 min)
        // ════════════════════════════════════════════════════════════════════
        private void StartBatteryTimer()
        {
            _batteryTimer?.Dispose();
            _batteryTimer = new Timer(async _ =>
            {
                if (IsConnected) await RequestBatteryLevelAsync();
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        private void StopBatteryTimer()
        {
            _batteryTimer?.Dispose();
            _batteryTimer = null;
        }

        // ════════════════════════════════════════════════════════════════════
        // AUTO-RECONNECT TIMER
        // ════════════════════════════════════════════════════════════════════
        private void StartReconnectTimer()
        {
            if (_lastDeviceId == null) return;
            StopReconnectTimer();
            _reconnectTimer = new Timer(async _ =>
            {
                if (!IsConnected && _lastDeviceId != null && _autoReconnect)
                {
                    Debug.WriteLine("[BT] Auto-reconnect attempt...");
                    await ConnectAsync(_lastDeviceId);
                }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        }

        private void StopReconnectTimer()
        {
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════
        public static bool IsPrinter(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var p in PrinterPrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopBatteryTimer();
            StopReconnectTimer();
            _watcher?.Stop();
            foreach (var ch in _subscribedChars)
            {
                try { ch.ValueChanged -= OnCharacteristicValueChanged; } catch { }
            }
            _subscribedChars.Clear();
            CleanupDevice();
        }
    }
}
