using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hardcodet.Wpf.TaskbarNotification;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Виртуальный сетевой принтер (аналог JetDirect) с интеграцией spooler.
    /// Принимает RAW данные от Windows Print Spooler и буферизирует через SpoolerManager.
    /// 
    /// Особенности:
    /// - Отслеживает состояние принтера и делает его активным/неактивным в системе
    /// - Показывает ошибки через balloon tip в трее
    /// - Автоматически возобновляет печать при восстановлении соединения
    /// </summary>
    public class NetworkPrinterServer : IDisposable
    {
        private readonly PrinterSpoolerManager _spooler;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        
        public const int DefaultPort = 9100;
        
        /// <summary>Порт для прослушивания (по умолчанию 9100, JetDirect).</summary>
        public int Port { get; private set; } = DefaultPort;
        
        /// <summary>Работает ли сервер.</summary>
        public bool IsRunning => _listener != null;
        
        /// <summary>Последняя ошибка.</summary>
        public string LastError => _spooler.LastError;
        
        /// <summary>Количество заданий в очереди.</summary>
        public int QueuedJobs => _spooler.QueuedJobsCount;
        
        /// <summary>Онлайн ли принтер.</summary>
        public bool IsPrinterOnline => _spooler.IsPrinterOnline;
        
        // WPF TaskbarIcon для показа balloon tips
        private TaskbarIcon? _trayIcon;
        
        /// <summary>Событие изменения статуса принтера.</summary>
        public event EventHandler<string>? StatusChanged;
        
        /// <summary>Событие ошибки печати.</summary>
        public event EventHandler<PrintErrorEventArgs>? PrintError;
        
        public NetworkPrinterServer(PrinterSpoolerManager spooler)
        {
            _spooler = spooler;
            _spooler.ErrorOccurred += OnSpoolerError;
            _spooler.JobQueued += OnJobQueued;
        }
        
        /// <summary>
        /// Устанавливает ссылку на TaskbarIcon для показа balloon tips.
        /// </summary>
        public void SetTrayIcon(TaskbarIcon? icon)
        {
            _trayIcon = icon;
        }
        
        /// <summary>
        /// Запускает TCP сервер на порту 9100.
        /// </summary>
        public void Start(int port = DefaultPort)
        {
            if (IsRunning) return;
            
            _cts = new CancellationTokenSource();
            Port = port;
            
            // Пробуем порт, если занят — следующий
            int actualPort = port;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Any, actualPort);
                    _listener.Start();
                    Port = actualPort;
                    break;
                }
                catch (SocketException ex)
                {
                    Debug.WriteLine($"[NetPrint] Port {actualPort} busy ({ex.Message}), trying {actualPort + 1}");
                    actualPort++;
                    _listener = null;
                }
            }
            
            if (_listener == null)
            {
                var error = "Не удалось запустить print server — все порты заняты";
                StatusChanged?.Invoke(this, error);
                ShowError("Ошибка сервера", error);
                return;
            }
            
            Debug.WriteLine($"[NetPrint] Listening on port {Port} (JetDirect protocol)");
            StatusChanged?.Invoke(this, $"Print server started on port {Port}");
            
            // Запускаем прослушивание
            _ = AcceptLoopAsync(_cts.Token);
        }
        
        /// <summary>
        /// Останавливает TCP сервер.
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            Debug.WriteLine("[NetPrint] Stopped");
            StatusChanged?.Invoke(this, "Print server stopped");
        }
        
        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(token);
                    _ = HandleClientAsync(client, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NetPrint] Accept error: {ex.Message}");
                }
            }
        }
        
        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            Debug.WriteLine($"[NetPrint] Client connected: {clientEndpoint}");
            StatusChanged?.Invoke(this, "Получение задания печати...");
            
            try
            {
                using var stream = client.GetStream();
                using var ms = new MemoryStream();
                
                var buffer = new byte[8192];
                int read;
                
                // Читаем данные от клиента (Windows Print Spooler)
                // Таймаут для чтения — 30 секунд
                var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(token, readTimeout.Token).Token;
                
                try
                {
                    while ((read = await stream.ReadAsync(buffer, linkedToken)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                    }
                }
                catch (OperationCanceledException) when (readTimeout.IsCancellationRequested)
                {
                    Debug.WriteLine("[NetPrint] Read timeout");
                }
                
                var data = ms.ToArray();
                Debug.WriteLine($"[NetPrint] Received {data.Length} bytes from {clientEndpoint}");
                
                if (data.Length == 0)
                {
                    StatusChanged?.Invoke(this, "Пустое задание печати");
                    return;
                }
                
                // Обрабатываем задание через spooler
                await ProcessPrintJobAsync(data);
            }
            catch (OperationCanceledException) { }
            catch (IOException ex)
            {
                Debug.WriteLine($"[NetPrint] IO error: {ex.Message}");
                StatusChanged?.Invoke(this, $"Ошибка: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetPrint] Handle error: {ex.Message}");
                StatusChanged?.Invoke(this, $"Ошибка обработки: {ex.Message}");
                ShowError("Ошибка печати", ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
                client.Dispose();
            }
        }
        
        private async Task ProcessPrintJobAsync(byte[] data)
        {
            if (!_spooler.IsPrinterOnline)
            {
                // Принтер не подключен
                var error = "Принтер не подключён. Задание сохранено в очередь.";
                StatusChanged?.Invoke(this, error);
                ShowError("Принтер отключён", "Проверьте Bluetooth соединение.\nЗадание будет напечатано автоматически при подключении.");
                PrintError?.Invoke(this, new PrintErrorEventArgs
                {
                    ErrorType = PrintErrorType.PrinterOffline,
                    Message = error,
                    IsRecoverable = true
                });
            }
            
            // Добавляем в очередь через spooler
            var success = await _spooler.QueuePrintJobAsync(data);
            
            if (success)
            {
                StatusChanged?.Invoke(this, "Печать завершена");
            }
            else
            {
                var error = _spooler.LastError;
                StatusChanged?.Invoke(this, $"Задание в очереди: {error}");
            }
        }
        
        private void OnSpoolerError(object? sender, string error)
        {
            ShowError("Ошибка печати", error);
            PrintError?.Invoke(this, new PrintErrorEventArgs
            {
                ErrorType = PrintErrorType.PrintFailed,
                Message = error,
                IsRecoverable = true
            });
        }
        
        private void OnJobQueued(object? sender, int count)
        {
            if (count > 0)
            {
                StatusChanged?.Invoke(this, $"В очереди: {count} задание(й)");
            }
        }
        
        /// <summary>
        /// Показывает balloon tip в трее.
        /// </summary>
        private void ShowError(string title, string message)
        {
            if (_trayIcon == null) return;
            
            _trayIcon.ShowBalloonTip(title, message, BalloonIcon.Warning);
        }
        
        /// <summary>
        /// Показывает информационное уведомление.
        /// </summary>
        public void ShowInfo(string title, string message)
        {
            if (_trayIcon == null) return;
            _trayIcon.ShowBalloonTip(title, message, BalloonIcon.Info);
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _spooler.ErrorOccurred -= OnSpoolerError;
            _spooler.JobQueued -= OnJobQueued;
            _spooler.Dispose();
            Stop();
            _cts?.Dispose();
        }
    }
    
    public class PrintErrorEventArgs : EventArgs
    {
        public PrintErrorType ErrorType { get; set; }
        public string Message { get; set; } = "";
        public bool IsRecoverable { get; set; }
    }
    
    public enum PrintErrorType
    {
        None,
        PrinterOffline,
        PrintFailed,
        ConnectionLost,
        Timeout
    }
}
