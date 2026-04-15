using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace MarklifeWin.Bluetooth
{
    public class RfcommPrinterManager : IPrinterManager
    {
        private static readonly string[] PrinterPrefixes = {
            "X2"
        };

        private static readonly Guid SppUuid = new("00001101-0000-1000-8000-00805F9B34FB");

        private BluetoothLEAdvertisementWatcher? _watcher;
        private readonly HashSet<ulong> _seenAddresses = new();

        private StreamSocket? _socket;
        private DataWriter? _writer;
        private DataReader? _reader;

        // Семафор — только одна команда одновременно
        private readonly SemaphoreSlim _cmdLock = new(1, 1);

        private Timer? _batteryTimer;
        private Timer? _reconnectTimer;
        private string? _lastDeviceId;
        private bool _autoReconnect;
        private bool _disposed;
        private bool _isConnecting; // защита от двойного подключения

        public event EventHandler<string>? DeviceDiscovered;
        public event EventHandler<bool>? ConnectionStateChanged;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<int>? BatteryLevelChanged;
        public event EventHandler<string?>? FirmwareReceived;
        public event EventHandler<string?>? SerialReceived;
        public event EventHandler<int?>? PaperLevelReceived;
        public event EventHandler<int?>? ShutdownTimeReceived;
        public event EventHandler<string?>? MacAddressReceived;

        public bool IsConnected => _socket != null && _writer != null;
        public string? ConnectedDeviceName { get; private set; }

        private async Task MonitorConnectionAsync()
        {
            while (_socket != null)
            {
                await Task.Delay(5000);

                // Проверяем, не занят ли сокет
                if (!_cmdLock.Wait(0)) continue;

                try
                {
                    if (_socket == null) break;

                    // Отправляем 1 байт (принтер его проигнорирует)
                    var buffer = new byte[] { 0x00 };
                    await _socket.OutputStream.WriteAsync(buffer.AsBuffer());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RFCOMM] Connection lost: {ex.Message}");
                    break;
                }
                finally
                {
                    _cmdLock.Release();
                }
            }

            CleanupSocket();
            ConnectionStateChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, "Принтер отключился");
        }

        public bool AutoReconnect
        {
            get => _autoReconnect;
            set { _autoReconnect = value; if (value && _lastDeviceId != null) StartReconnectTimer(); else StopReconnectTimer(); }
        }

        public string? LastDeviceId
        {
            get => _lastDeviceId;
            set { _lastDeviceId = value; if (_autoReconnect && value != null) StartReconnectTimer(); }
        }

        // ── SCAN ─────────────────────────────────────────────────────────────
        public async Task ScanAsync(int durationMs = 8000)
        {
            StatusChanged?.Invoke(this, "Сканирование...");
            _seenAddresses.Clear();
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
                Debug.WriteLine($"[RFCOMM] Scan error: {ex.Message}");
                StatusChanged?.Invoke(this, $"Ошибка сканирования: {ex.Message}");
            }
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender,
                                              BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName;
            if (string.IsNullOrEmpty(name) || !IsPrinter(name)) return;
            if (!_seenAddresses.Add(args.BluetoothAddress)) return;
            Debug.WriteLine($"[RFCOMM] BLE found: {name} ({args.BluetoothAddress})");
            DeviceDiscovered?.Invoke(this, $"{name}|{args.BluetoothAddress}");
        }

        // ── CONNECT ──────────────────────────────────────────────────────────
        public async Task ConnectAsync(string deviceId)
        {
            // Защита от двойного подключения
            if (_isConnecting) { Debug.WriteLine("[RFCOMM] Already connecting, skip"); return; }
            _isConnecting = true;

            // Закрываем предыдущее соединение если было
            if (IsConnected)
            {
                Debug.WriteLine("[RFCOMM] Already connected, cleaning up first");
                StopBatteryTimer();
                CleanupSocket();
            }

            StatusChanged?.Invoke(this, "Подключение...");
            try
            {
                BluetoothDevice? btDevice = null;
                if (ulong.TryParse(deviceId, out ulong address))
                    btDevice = await BluetoothDevice.FromBluetoothAddressAsync(address);
                else
                    btDevice = await BluetoothDevice.FromIdAsync(deviceId);

                if (btDevice == null)
                {
                    StatusChanged?.Invoke(this, "Устройство не найдено");
                    ConnectionStateChanged?.Invoke(this, false);
                    return;
                }

                ConnectedDeviceName = btDevice.Name;
                Debug.WriteLine($"[RFCOMM] Device: {btDevice.Name}, paired={btDevice.DeviceInformation.Pairing.IsPaired}");

                // Уведомляем UI что устройство найдено (для отображения в списке)
                DeviceDiscovered?.Invoke(this, $"{btDevice.Name}|{deviceId}");

                // Ищем SPP сервис
                var sppResult = await btDevice.GetRfcommServicesForIdAsync(
                    RfcommServiceId.FromUuid(SppUuid), BluetoothCacheMode.Uncached);
                Debug.WriteLine($"[RFCOMM] SPP: count={sppResult.Services.Count}, error={sppResult.Error}");

                RfcommDeviceService? service = null;
                if (sppResult.Error == BluetoothError.Success && sppResult.Services.Count > 0)
                {
                    service = sppResult.Services[0];
                }
                else
                {
                    var allResult = await btDevice.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                    Debug.WriteLine($"[RFCOMM] All RFCOMM services: {allResult.Services.Count}");
                    foreach (var s in allResult.Services)
                        Debug.WriteLine($"[RFCOMM]   {s.ServiceId.Uuid}");
                    if (allResult.Services.Count > 0)
                        service = allResult.Services[0];
                }

                if (service == null)
                {
                    StatusChanged?.Invoke(this, "RFCOMM/SPP сервис не найден. Убедитесь что принтер спарен.");
                    ConnectionStateChanged?.Invoke(this, false);
                    return;
                }

                _socket = new StreamSocket();
                await _socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName);

                _writer = new DataWriter(_socket.OutputStream);
                _reader = new DataReader(_socket.InputStream)
                {
                    InputStreamOptions = InputStreamOptions.Partial
                };

                _lastDeviceId = deviceId;
                _isConnecting = false;
                ConnectionStateChanged?.Invoke(this, true);
                StatusChanged?.Invoke(this, $"Подключен к {ConnectedDeviceName}");

                await Task.Delay(300);
                await RequestBatteryLevelAsync();
                StartBatteryTimer();
                await Task.Delay(3000);
                _ = Task.Run(() => MonitorConnectionAsync());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RFCOMM] Connect error: {ex}");
                StatusChanged?.Invoke(this, $"Ошибка подключения: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, false);
                CleanupSocket();
            }
            finally
            {
                _isConnecting = false;
            }
        }

        // ── DISCONNECT ───────────────────────────────────────────────────────
        public async Task DisconnectAsync()
        {
            StopBatteryTimer();
            StopReconnectTimer();
            CleanupSocket();
            ConnectionStateChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, "Отключен");
            await Task.CompletedTask;
        }

        public void Disconnect() => _ = DisconnectAsync();

        private void CleanupSocket()
        {
            ConnectedDeviceName = null;
            // Сначала зануляем, потом dispose — параллельные вызовы увидят null
            var writer = _writer; _writer = null;
            var reader = _reader; _reader = null;
            var socket = _socket; _socket = null;
            try { writer?.Dispose(); } catch { }
            try { reader?.Dispose(); } catch { }
            try { socket?.Dispose(); } catch { }
        }

        // ════════════════════════════════════════════════════════════════════
        // CORE: отправить команду и прочитать ответ как стрим
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Отправляет команду и читает ответ до таймаута.
        /// RFCOMM — стрим, читаем сколько придёт за отведённое время.
        /// </summary>
        private async Task<byte[]?> SendAndReceiveAsync(byte[] command, int timeoutMs)
        {
            if (_writer == null || _reader == null) return null;

            await _cmdLock.WaitAsync();
            try
            {
                if (_writer == null || _reader == null) return null;

                _writer.WriteBytes(command);
                await _writer.StoreAsync();
                Debug.WriteLine($"[RFCOMM] >> {BitConverter.ToString(command)}");

                using var cts = new CancellationTokenSource(timeoutMs);
                var result = new List<byte>();
                bool timedOut = false;

                try
                {
                    uint loaded = await _reader.LoadAsync(512).AsTask(cts.Token);
                    if (loaded > 0)
                    {
                        var chunk = new byte[loaded];
                        _reader.ReadBytes(chunk);
                        result.AddRange(chunk);
                        Debug.WriteLine($"[RFCOMM] << {BitConverter.ToString(chunk)}");
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine($"[RFCOMM] Read timeout after {timeoutMs}ms");
                    timedOut = true;
                }

                if (timedOut && _socket != null)
                {
                    try { _reader?.Dispose(); } catch { }
                    _reader = new DataReader(_socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
                    Debug.WriteLine("[RFCOMM] DataReader recreated after timeout");
                }

                return result.Count > 0 ? result.ToArray() : null;
            }
            catch (ObjectDisposedException)
            {
                Debug.WriteLine("[RFCOMM] Socket disposed during command, ignoring");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RFCOMM] SendAndReceive error: {ex.Message}");
                return null;
            }
            finally
            {
                _cmdLock.Release();
            }
        }

        private async Task SendRawAsync(byte[] command)
        {
            if (_writer == null) return;
            await _cmdLock.WaitAsync();
            try
            {
                _writer.WriteBytes(command);
                await _writer.StoreAsync();
                Debug.WriteLine($"[RFCOMM] >> raw {BitConverter.ToString(command)}");
            }
            catch (Exception ex) { Debug.WriteLine($"[RFCOMM] Raw send error: {ex.Message}"); }
            finally { _cmdLock.Release(); }
        }

        // ════════════════════════════════════════════════════════════════════
        // COMMANDS
        // ════════════════════════════════════════════════════════════════════
        public async Task RequestBatteryLevelAsync()
        {
            if (!IsConnected) return;
            var data = await SendAndReceiveAsync(new byte[] { 0x10, 0xFF, 0x50, 0xF1 }, 2000);
            if (data == null || !IsConnected) return;
            Debug.WriteLine($"[RFCOMM] Battery raw ({data.Length}b): {BitConverter.ToString(data)}");
            // Ответ: 00 XX где XX = процент (0-100). Игнорируем "OK" и мусор.
            if (data.Length >= 2 && data[0] == 0x00 && data[1] <= 100)
                BatteryLevelChanged?.Invoke(this, data[1]);
            else
                Debug.WriteLine("[RFCOMM] Battery: unexpected response, ignoring");
        }

        public async Task RequestAllInfoAsync()
        {
            if (!IsConnected) return;

            await RequestBatteryLevelAsync();
            if (!IsConnected) return;
            await Task.Delay(500);

            var fw = await SendAndReceiveAsync(new byte[] { 0x10, 0xFF, 0x20, 0xF1 }, 2000);
            if (!IsConnected) return;
            if (fw?.Length > 0)
            {
                var ver = System.Text.Encoding.ASCII.GetString(fw).TrimEnd('\0', '\r', '\n');
                Debug.WriteLine($"[RFCOMM] Firmware: {ver}");
                FirmwareReceived?.Invoke(this, ver);
            }
            await Task.Delay(500);

            var sn = await SendAndReceiveAsync(new byte[] { 0x10, 0xFF, 0x20, 0xF2 }, 2000);
            if (!IsConnected) return;
            if (sn?.Length > 0)
            {
                int end = Array.IndexOf(sn, (byte)0);
                var serial = System.Text.Encoding.ASCII.GetString(sn, 0, end < 0 ? sn.Length : end);
                Debug.WriteLine($"[RFCOMM] Serial: {serial}");
                SerialReceived?.Invoke(this, serial);
            }
            await Task.Delay(500);

            var paper = await SendAndReceiveAsync(new byte[] { 0x1A, 0x1F, 0x06 }, 15000);
            if (!IsConnected) return;
            if (paper != null)
            {
                Debug.WriteLine($"[RFCOMM] Paper raw ({paper.Length}b): {BitConverter.ToString(paper)}");
                if (paper.Length >= 8 && paper[0] == 0x1A && paper[1] == 0x1F && paper[2] == 0x06)
                {
                    // Checksum validation: sum of bytes[3..6]
                    byte checksum = (byte)(paper[3] + paper[4] + paper[5] + paper[6]);
                    if (checksum == paper[7])
                    {
                        int used = ((paper[3] & 0xFF) << 8) | (paper[4] & 0xFF);
                        int total = ((paper[5] & 0xFF) << 8) | (paper[6] & 0xFF);
                        // pct = total / used * 100 (total=max capacity, used=current remaining)
                        int pct = used > 0 ? (int)Math.Round(total * 100.0 / used) : 0;
                        pct = Math.Min(100, Math.Max(0, pct));
                        Debug.WriteLine($"[RFCOMM] Paper: used={used}, total={total}, pct={pct}%");
                        PaperLevelReceived?.Invoke(this, pct);
                    }
                    else
                    {
                        Debug.WriteLine($"[RFCOMM] Paper checksum mismatch: expected={paper[7]:X2}, got={checksum:X2}");
                    }
                }
            }
            await Task.Delay(500);

            var sd = await SendAndReceiveAsync(new byte[] { 0x10, 0xFF, 0x13 }, 5000);
            if (!IsConnected) return;
            if (sd != null)
            {
                Debug.WriteLine($"[RFCOMM] Shutdown raw ({sd.Length}b): {BitConverter.ToString(sd)}");
                if (sd.Length >= 1 && sd[0] > 0)
                    ShutdownTimeReceived?.Invoke(this, sd[0]);
            }
            await Task.Delay(500);

            var mac = await SendAndReceiveAsync(new byte[] { 0x10, 0xFF, 0x20, 0xF3 }, 5000);
            if (!IsConnected) return;
            if (mac?.Length >= 6)
            {
                var macStr = BitConverter.ToString(mac, 0, 6).Replace("-", ":");
                Debug.WriteLine($"[RFCOMM] MAC: {macStr}");
                MacAddressReceived?.Invoke(this, macStr);
            }
        }

        public async Task SetShutdownTimeAsync(int minutes)
        {
            byte hi = (byte)(minutes / 256);
            byte lo = (byte)(minutes % 256);
            // Отправляем и читаем ответ (принтер отвечает "OK"), потом пауза
            await SendAndReceiveAsync(new byte[] { 0x10, 0xFF, 0x12, hi, lo }, 1000);
            await Task.Delay(1000);
            Debug.WriteLine($"[RFCOMM] Shutdown time set: {minutes} min");
        }

        public async Task SendDataAsync(byte[] data)
        {
            if (_writer == null) { StatusChanged?.Invoke(this, "Принтер не подключен"); return; }
            await _cmdLock.WaitAsync();
            try
            {
                int chunk = 4096;
                int start = 0;

                // Marklife: первые 4 байта — запрос статуса, принтер должен ответить
                if (data.Length > 4 && data[0] == 0x10 && data[1] == 0xFF)
                {
                    _writer.WriteBytes(new byte[] { data[0], data[1], data[2], data[3] });
                    await _writer.StoreAsync();
                    Debug.WriteLine($"[RFCOMM] >> status request {BitConverter.ToString(data, 0, 32)}");
                    await Task.Delay(300); // ждём ответ принтера
                    start = 4;
                }

                for (int i = start; i < data.Length; i += chunk)
                {
                    if (_writer == null) break;
                    int len = Math.Min(chunk, data.Length - i);
                    var buf = new byte[len];
                    Array.Copy(data, i, buf, 0, len);
                    _writer.WriteBytes(buf);
                    await _writer.StoreAsync();
                    Debug.WriteLine($"[RFCOMM] >> chunk {i}-{i + len} ({len}b)");
                    await Task.Delay(20);
                }
                StatusChanged?.Invoke(this, "Данные отправлены");
            }
            catch (Exception ex) { StatusChanged?.Invoke(this, $"Ошибка: {ex.Message}"); }
            finally { _cmdLock.Release(); }
        }

        // ── TIMERS ───────────────────────────────────────────────────────────
        private void StartBatteryTimer()
        {
            _batteryTimer?.Dispose();
            _batteryTimer = new Timer(async _ => { if (IsConnected) await RequestBatteryLevelAsync(); },
                null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        private void StopBatteryTimer() { _batteryTimer?.Dispose(); _batteryTimer = null; }

        private void StartReconnectTimer()
        {
            if (_lastDeviceId == null) return;
            StopReconnectTimer();
            _reconnectTimer = new Timer(async _ =>
            {
                if (!IsConnected && _autoReconnect && _lastDeviceId != null)
                    await ConnectAsync(_lastDeviceId);
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        }
        private void StopReconnectTimer() { _reconnectTimer?.Dispose(); _reconnectTimer = null; }

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
            CleanupSocket();
        }
    }
}
