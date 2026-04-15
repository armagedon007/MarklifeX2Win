using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MarklifeWin.Bluetooth;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Управляет spooling'ом заданий печати с буферизацией и повтором при восстановлении соединения.
    /// Аналог cups-filter в macOS/Linux — буферизирует задания когда принтер недоступен.
    /// </summary>
    public class PrinterSpoolerManager : IDisposable
    {
        private readonly IPrinterManager _bt;
        private readonly string _spoolDirectory;
        private readonly object _lock = new();
        
        private bool _printerConnected;
        private string _lastError = "";
        private Timer? _retryTimer;
        private Timer? _statusTimer;
        private bool _disposed;
        
        // Состояние принтера для Windows
        public event EventHandler<PrinterStatusChangedEventArgs>? PrinterStatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<int>? JobQueued;
        
        public bool IsPrinterOnline => _printerConnected;
        public string LastError => _lastError;
        public int QueuedJobsCount
        {
            get
            {
                lock (_lock)
                    return GetSpoolFiles().Count;
            }
        }
        
        public PrinterSpoolerManager(IPrinterManager bt)
        {
            _bt = bt;
            
            // Spool директория в AppData
            _spoolDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarklifeX2", "spool");
            
            Directory.CreateDirectory(_spoolDirectory);
            
            // Подписываемся на события Bluetooth
            _bt.ConnectionStateChanged += OnConnectionStateChanged;
            
            // Запускаем периодическую проверку статуса принтера
            _statusTimer = new Timer(_ => CheckPrinterStatus(), null, 
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        }
        
        private void OnConnectionStateChanged(object? sender, bool connected)
        {
            var wasConnected = _printerConnected;
            _printerConnected = connected;
            
            Debug.WriteLine($"[Spooler] Connection changed: {connected}");
            
            if (connected)
            {
                // Принтер подключен — обновляем статус и запускаем обработку очереди
                SetPrinterStatus(true, "Готов");
                _lastError = "";
                
                // Запускаем обработку буферизованных заданий
                ProcessSpoolQueue();
            }
            else
            {
                // Принтер отключен
                SetPrinterStatus(false, "Отключен");
                
                // Запускаем таймер повторных попыток подключения
                StartRetryTimer();
            }
        }
        
        private void SetPrinterStatus(bool online, string status)
        {
            PrinterStatusChanged?.Invoke(this, new PrinterStatusChangedEventArgs
            {
                IsOnline = online,
                StatusText = status,
                ErrorMessage = online ? "" : _lastError
            });
            
            // Обновляем состояние принтера в Windows через WMI
            UpdatePrinterInWindows(online, status);
        }
        
        /// <summary>
        /// Обновляет состояние принтера Marklife X2 в Windows через WMI.
        /// </summary>
        private void UpdatePrinterInWindows(bool online, string status)
        {
            try
            {
                // Используем PowerShell для обновления принтера
                var script = $@"
                    try {{
                        $printer = Get-Printer -Name 'Marklife X2' -ErrorAction Stop
                        if ('{online}' -eq 'True') {{
                            # Принтер онлайн - устанавливаем статус Ready
                            $printer.Comment = 'Online'
                        }} else {{
                            # Принтер офлайн
                            $printer.Comment = 'Offline: {status}'
                        }}
                    }} catch {{
                        # Принтер не найден - игнорируем
                    }}
                ";
                
                // Асинхронное выполнение без блокировки UI
                Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        
                        using var process = Process.Start(psi);
                        process?.WaitForExit(1000);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Spooler] WMI update error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Spooler] Printer status update failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Добавляет задание в spool и пытается напечатать.
        /// Если принтер недоступен — буферизирует.
        /// </summary>
        public async Task<bool> QueuePrintJobAsync(byte[] data, string jobName = "Marklife Print")
        {
            var spoolFile = Path.Combine(_spoolDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.spl");
            
            try
            {
                // Всегда сохраняем в spool
                await File.WriteAllBytesAsync(spoolFile, data);
                
                Debug.WriteLine($"[Spooler] Job queued: {Path.GetFileName(spoolFile)} ({data.Length} bytes)");
                JobQueued?.Invoke(this, QueuedJobsCount);
                
                if (_printerConnected && _bt.IsConnected)
                {
                    // Принтер доступен — печатаем сразу
                    return await ProcessJobAsync(spoolFile);
                }
                else
                {
                    // Принтер недоступен — задание буферизировано
                    _lastError = "Принтер недоступен. Задание сохранено в очередь.";
                    ErrorOccurred?.Invoke(this, _lastError);
                    SetPrinterStatus(false, "Ожидание принтера");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _lastError = $"Ошибка добавления задания: {ex.Message}";
                ErrorOccurred?.Invoke(this, _lastError);
                Debug.WriteLine($"[Spooler] Queue error: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Обрабатывает буферизованные задания.
        /// </summary>
        private async void ProcessSpoolQueue()
        {
            if (!_printerConnected) return;
            
            var files = GetSpoolFiles();
            if (files.Count == 0) return;
            
            Debug.WriteLine($"[Spooler] Processing {files.Count} queued jobs");
            
            foreach (var file in files)
            {
                if (!_printerConnected || !_bt.IsConnected)
                {
                    Debug.WriteLine("[Spooler] Printer disconnected during queue processing");
                    break;
                }
                
                await ProcessJobAsync(file);
            }
        }
        
        private List<string> GetSpoolFiles()
        {
            lock (_lock)
            {
                try
                {
                    return Directory.GetFiles(_spoolDirectory, "*.spl")
                        .OrderBy(f => f)
                        .ToList();
                }
                catch
                {
                    return new List<string>();
                }
            }
        }
        
        private async Task<bool> ProcessJobAsync(string spoolFile)
        {
            if (!File.Exists(spoolFile))
            {
                Debug.WriteLine($"[Spooler] Spool file not found: {spoolFile}");
                return false;
            }
            
            try
            {
                var data = await File.ReadAllBytesAsync(spoolFile);
                Debug.WriteLine($"[Spooler] Printing {data.Length} bytes from {Path.GetFileName(spoolFile)}");
                
                SetPrinterStatus(true, "Печать...");
                
                // Отправляем данные через Bluetooth
                await _bt.SendDataAsync(data);
                
                // Удаляем успешно обработанный файл
                File.Delete(spoolFile);
                
                Debug.WriteLine($"[Spooler] Job completed: {Path.GetFileName(spoolFile)}");
                JobQueued?.Invoke(this, QueuedJobsCount);
                SetPrinterStatus(true, "Готов");
                
                return true;
            }
            catch (Exception ex)
            {
                _lastError = $"Ошибка печати: {ex.Message}";
                ErrorOccurred?.Invoke(this, _lastError);
                SetPrinterStatus(false, "Ошибка печати");
                
                Debug.WriteLine($"[Spooler] Print error: {ex.Message}");
                
                // Останавливаем обработку очереди при ошибке
                return false;
            }
        }
        
        private void StartRetryTimer()
        {
            _retryTimer?.Dispose();
            _retryTimer = new Timer(async _ =>
            {
                if (_disposed) return;
                
                if (_printerConnected && _bt.IsConnected)
                {
                    // Принтер снова доступен
                    Debug.WriteLine("[Spooler] Printer reconnected, processing queue");
                    ProcessSpoolQueue();
                    _retryTimer?.Dispose();
                    _retryTimer = null;
                }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        }
        
        private void CheckPrinterStatus()
        {
            // Периодическая проверка состояния
            var isOnline = _bt.IsConnected;
            if (isOnline != _printerConnected)
            {
                OnConnectionStateChanged(this, isOnline);
            }
        }
        
        /// <summary>
        /// Очищает spool директорию.
        /// </summary>
        public void ClearSpool()
        {
            lock (_lock)
            {
                foreach (var file in Directory.GetFiles(_spoolDirectory, "*.spl"))
                {
                    try { File.Delete(file); }
                    catch { }
                }
            }
            JobQueued?.Invoke(this, 0);
        }
        
        /// <summary>
        /// Получает информацию о принтере для отображения в системе.
        /// </summary>
        public PrinterInfo GetPrinterInfo()
        {
            return new PrinterInfo
            {
                Name = "Marklife X2",
                IsOnline = _printerConnected,
                Status = _printerConnected ? "Готов" : "Отключен",
                ErrorMessage = _lastError,
                QueuedJobs = QueuedJobsCount,
                SpoolPath = _spoolDirectory
            };
        }
        
        private void CheckPrinterStatus(object? state)
        {
            CheckPrinterStatus();
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _retryTimer?.Dispose();
            _statusTimer?.Dispose();
            _bt.ConnectionStateChanged -= OnConnectionStateChanged;
        }
    }
    
    public class PrinterStatusChangedEventArgs : EventArgs
    {
        public bool IsOnline { get; set; }
        public string StatusText { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }
    
    public class PrinterInfo
    {
        public string Name { get; set; } = "";
        public bool IsOnline { get; set; }
        public string Status { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        public int QueuedJobs { get; set; }
        public string SpoolPath { get; set; } = "";
    }
}
