using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Следит за папкой куда XPS Document Writer пишет файлы.
    /// При появлении нового .xps файла — рендерит и отправляет на принтер.
    /// </summary>
    public class XpsPrintWatcher : IDisposable
    {
        private readonly PrintEngine      _engine;
        private readonly string           _watchFolder;
        private FileSystemWatcher?        _watcher;
        private bool                      _disposed;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? PrintError;

        public static string DefaultWatchFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Marklife", "PrintJobs");

        public XpsPrintWatcher(PrintEngine engine, string? watchFolder = null)
        {
            _engine      = engine;
            _watchFolder = watchFolder ?? DefaultWatchFolder;
            Directory.CreateDirectory(_watchFolder);
        }

        public void Start()
        {
            _watcher = new FileSystemWatcher(_watchFolder, "*.xps")
            {
                NotifyFilter        = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnFileCreated;
            Debug.WriteLine($"[XpsWatcher] Watching: {_watchFolder}");
            StatusChanged?.Invoke(this, $"XPS watcher started: {_watchFolder}");
        }

        public void Stop()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private async void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            Debug.WriteLine($"[XpsWatcher] New file: {e.FullPath}");

            // Ждём пока файл полностью записан
            await Task.Delay(500);
            if (!await WaitForFileReady(e.FullPath, 10000)) return;

            StatusChanged?.Invoke(this, $"Processing: {Path.GetFileName(e.FullPath)}");

            try
            {
                // Читаем настройки из PrintTicket
                var settings = PrintJobSettings.FromPrintQueue(PrintEngine.PrinterName);
                int w = settings.PaperWidthMm  > 0 ? settings.PaperWidthMm  : 40;
                int h = settings.PaperHeightMm > 0 ? settings.PaperHeightMm : 30;

                Debug.WriteLine($"[XpsWatcher] Rendering {w}x{h}mm, copies={settings.Copies}");

                // Рендерим XPS в Marklife команды
                var commands = XpsRenderer.RenderXpsToPrintCommands(e.FullPath, w, h);
                if (commands == null)
                {
                    PrintError?.Invoke(this, "Failed to render XPS file");
                    return;
                }

                Debug.WriteLine($"[XpsWatcher] Rendered: {commands.Length} bytes");

                // Отправляем нужное количество копий
                int copies = settings.Copies > 0 ? settings.Copies : 1;
                for (int i = 0; i < copies; i++)
                {
                    await _engine.PrintRawAsync(commands);
                    if (copies > 1 && i < copies - 1)
                        await Task.Delay(500);
                }

                StatusChanged?.Invoke(this, "Print job done");

                // Удаляем обработанный файл
                try { File.Delete(e.FullPath); } catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[XpsWatcher] Error: {ex.Message}");
                PrintError?.Invoke(this, $"Print error: {ex.Message}");
            }
        }

        private static async Task<bool> WaitForFileReady(string path, int timeoutMs)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                try
                {
                    using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return true;
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                }
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
