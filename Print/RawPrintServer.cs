using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarklifeWin.Print
{
    public class RawPrintServer : IDisposable
    {
        private readonly PrintEngine _engine;
        private readonly PrintQueueManager _queue;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private int _activePort;

        public const int DefaultPort = 9200;
        public bool IsRunning { get; private set; }
        public int Port => _activePort;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? PrintJobReceived;
        public event EventHandler<string>? PrintJobError;

        public RawPrintServer(PrintEngine engine, PrintQueueManager queue)
        {
            _engine = engine;
            _queue = queue;
        }

        public void Start(int port = DefaultPort)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();

            for (int p = port; p < port + 10; p++)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, p);
                    _listener.Start();
                    _activePort = p;
                    break;
                }
                catch (SocketException)
                {
                    Debug.WriteLine($"[PrintServer] Port {p} busy");
                    _listener = null;
                }
            }

            if (_listener == null)
            {
                StatusChanged?.Invoke(this, "Print server: no available port");
                return;
            }

            IsRunning = true;
            Debug.WriteLine($"[PrintServer] Listening on port {_activePort}");
            _ = AcceptLoopAsync(_cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            IsRunning = false;
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(token);
                    _ = HandleJobAsync(client, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Debug.WriteLine($"[PrintServer] Accept: {ex.Message}"); }
            }
        }

        private async Task HandleJobAsync(TcpClient client, CancellationToken token)
        {
            Debug.WriteLine($"[PrintServer] Job from {client.Client.RemoteEndPoint}");
            try
            {
                using var stream = client.GetStream();
                using var ms = new MemoryStream();
                var buffer = new byte[8192];

                client.ReceiveTimeout = 30000;
                client.NoDelay = true;

                var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(30000);

                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                        var completedTask = await Task.WhenAny(readTask, Task.Delay(5000, cts.Token));

                        if (completedTask == readTask)
                        {
                            int bytesRead = await readTask;
                            if (bytesRead == 0) break;
                            ms.Write(buffer, 0, bytesRead);
                            Debug.WriteLine($"[PrintServer] Read {bytesRead} bytes, total {ms.Length}");
                        }
                        else
                        {
                            Debug.WriteLine($"[PrintServer] Read timeout, total {ms.Length} bytes");
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine($"[PrintServer] Read cancelled, total {ms.Length} bytes");
                }

                var data = ms.ToArray();
                Debug.WriteLine($"[PrintServer] Job size: {data.Length} bytes");

                if (data.Length == 0) return;

                var preview = BitConverter.ToString(data, 0, Math.Min(32, data.Length));
                Debug.WriteLine($"[PrintServer] First bytes: {preview}");

                // Check if this is IPP request (starts with HTTP or binary IPP)
                if (IsIppRequest(data))
                {
                    Debug.WriteLine("[PrintServer] IPP request detected");
                    await HandleIppRequestAsync(client, data);
                    return;
                }

                // Original raw printing logic
                await ProcessPrintDataAsync(data);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PrintServer] Job error: {ex.Message}");
                PrintJobError?.Invoke(this, $"Print error: {ex.Message}");
            }
            finally { client.Dispose(); }
        }

        private bool IsIppRequest(byte[] data)
        {
            if (data.Length < 5) return false;

            // Check for HTTP GET/POST
            string start = Encoding.ASCII.GetString(data, 0, Math.Min(5, data.Length));
            if (start.StartsWith("GET") || start.StartsWith("POST")) return true;

            // Check for binary IPP (version 0x01 0x01)
            if (data[0] == 0x01 && data[1] == 0x01) return true;

            // Check for PDF (starts with %PDF)
            if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46) return true;

            return false;
        }

        private async Task HandleIppRequestAsync(TcpClient client, byte[] data)
        {
            try
            {
                using var stream = client.GetStream();

                // Extract PDF from IPP request
                byte[]? pdfData = ExtractPdfFromIpp(data);

                if (pdfData != null && pdfData.Length > 0)
                {
                    Debug.WriteLine($"[PrintServer] PDF extracted from IPP: {pdfData.Length} bytes");

                    // Save for debugging
                    var debugPath = Path.Combine(Path.GetTempPath(), $"ipp_pdf_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
                    await File.WriteAllBytesAsync(debugPath, pdfData);
                    Debug.WriteLine($"[PrintServer] PDF saved to {debugPath}");

                    await ProcessPrintDataAsync(pdfData);
                }
                else
                {
                    // Send IPP response and continue reading
                    byte[] response = BuildIppResponse();
                    await stream.WriteAsync(response, 0, response.Length);
                    await stream.FlushAsync();
                    Debug.WriteLine("[PrintServer] IPP response sent");

                    // Try to read more data (the actual print job)
                    var buffer = new byte[65536];
                    var ms = new MemoryStream();
                    var cts = new CancellationTokenSource(10000);

                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (stream.DataAvailable)
                        {
                            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                            if (read == 0) break;
                            ms.Write(buffer, 0, read);
                            Debug.WriteLine($"[PrintServer] Read additional {read} bytes");
                        }
                        else
                        {
                            await Task.Delay(100);
                        }
                    }

                    var additionalData = ms.ToArray();
                    if (additionalData.Length > 0)
                    {
                        pdfData = ExtractPdfFromIpp(additionalData);
                        if (pdfData != null)
                        {
                            await ProcessPrintDataAsync(pdfData);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PrintServer] IPP handling error: {ex.Message}");
                PrintJobError?.Invoke(this, $"IPP error: {ex.Message}");
            }
        }

        private byte[]? ExtractPdfFromIpp(byte[] data)
        {
            // Look for PDF signature %PDF
            for (int i = 0; i < data.Length - 4; i++)
            {
                if (data[i] == 0x25 && data[i + 1] == 0x50 &&
                    data[i + 2] == 0x44 && data[i + 3] == 0x46)
                {
                    byte[] pdfData = new byte[data.Length - i];
                    Array.Copy(data, i, pdfData, 0, pdfData.Length);
                    return pdfData;
                }
            }
            return null;
        }

        private byte[] BuildIppResponse()
        {
            string httpHeader = "HTTP/1.1 200 OK\r\n" +
                                "Content-Type: application/ipp\r\n" +
                                "Content-Length: 131\r\n" +
                                "\r\n";

            byte[] ippBody = new byte[]
            {
                0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x01, 0x47, 0x42, 0x0B, 0x00, 0x1B, 0x69, 0x70, 0x70, 0x3A,
                0x2F, 0x2F, 0x6C, 0x6F, 0x63, 0x61, 0x6C, 0x68, 0x6F, 0x73,
                0x74, 0x3A, 0x36, 0x33, 0x31, 0x2F, 0x70, 0x72, 0x69, 0x6E,
                0x74, 0x65, 0x72, 0x00, 0x42, 0x0C, 0x00, 0x01, 0x03, 0x42,
                0x15, 0x00, 0x00, 0x42, 0x0C, 0x00, 0x0C, 0x58, 0x32, 0x20,
                0x50, 0x72, 0x69, 0x6E, 0x74, 0x20, 0x4C, 0x61, 0x62, 0x65,
                0x6C, 0x42, 0x0F, 0x00, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00,
                0x00, 0x00, 0x63, 0x03, 0x00
            };

            byte[] headerBytes = Encoding.ASCII.GetBytes(httpHeader);
            byte[] result = new byte[headerBytes.Length + ippBody.Length];
            Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
            Buffer.BlockCopy(ippBody, 0, result, headerBytes.Length, ippBody.Length);
            return result;
        }

        private async Task ProcessPrintDataAsync(byte[] data)
        {
            PrintJobReceived?.Invoke(this, $"Printing {data.Length} bytes...");
            StatusChanged?.Invoke(this, $"Printing {data.Length} bytes");

            if (PwgRasterRenderer.IsPwgRaster(data))
            {
                Debug.WriteLine("[PrintServer] PWG Raster detected");
                await PrintPwgAsync(data);
            }
            else if (IsXps(data))
            {
                Debug.WriteLine("[PrintServer] XPS format detected");
                await PrintXpsAsync(data);
            }
            else if (PdfRenderer.IsPdf(data))
            {
                Debug.WriteLine("[PrintServer] PDF format detected");
                await PrintPdfAsync(data);
            }
            else
            {
                Debug.WriteLine($"[PrintServer] Unknown format, sending raw");
                await _engine.PrintRawAsync(data);
                //DDDDDD
                string savePath = @"C:\temp\print_jobs";
                string filePath = Path.Combine(savePath, $"command_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
                string filePath2 = Path.Combine(savePath, $"command_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllBytes(filePath, data);
                string hex = BitConverter.ToString(data).Replace("-", " ");
                File.WriteAllText(filePath2, hex);
            }

            StatusChanged?.Invoke(this, "Print job done");
            Debug.WriteLine("[PrintServer] Job done");
        }

        private async Task PrintPwgAsync(byte[] data)
        {
            var settings = PrintJobSettings.FromPrintQueue(PrintQueueManager.PrinterName);
            int w = settings.PaperWidthMm > 0 ? settings.PaperWidthMm : 40;
            int h = settings.PaperHeightMm > 0 ? settings.PaperHeightMm : 30;

            var escpos = PwgRasterRenderer.ConvertToEscPos(data);
            if (escpos == null) { PrintJobError?.Invoke(this, "Failed to render PWG"); return; }

            int copies = settings.Copies > 0 ? settings.Copies : 1;
            for (int i = 0; i < copies; i++)
            {
                await _engine.PrintRawAsync(escpos);
                if (copies > 1 && i < copies - 1) await Task.Delay(500);
            }
        }

        private void GetPdfDetails(byte[] data, JobDetails details)
        {
            try
            {
                using var stream = new MemoryStream(data);
                using var document = PdfiumViewer.PdfDocument.Load(stream);

                details.PageCount = document.PageCount;

                // Размер первой страницы в пунктах (1 pt = 1/72 дюйма)
                var pageSize = document.PageSizes[0];
                details.WidthMm = (int)Math.Round(pageSize.Width / 72.0 * 25.4, MidpointRounding.AwayFromZero);
                details.HeightMm = (int)Math.Round(pageSize.Height / 72.0 * 25.4, MidpointRounding.AwayFromZero);

                // Проверка на цвет (приблизительно)
                details.IsColor = false;//CheckIfPdfIsColor(data);

                // Ориентация
                details.Orientation = details.WidthMm > details.HeightMm ? "Landscape" : "Portrait";

                // Копии из PDF не извлечь — оставляем 1
                details.Copies = 1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PDF details error: {ex.Message}");
            }
        }

        private async Task PrintPdfAsync(byte[] data)
        {
            var settings = new JobDetails();
            GetPdfDetails(data, settings);

            var w = settings.WidthMm;
            var h = settings.HeightMm;

            Debug.WriteLine($"w={w} h={h}");

            var commands = PdfRenderer.RenderToPrintCommands(data, w, h);
            if (commands == null) { PrintJobError?.Invoke(this, "Failed to render PDF"); return; }

            int copies = settings.Copies > 0 ? settings.Copies : 1;
            for (int i = 0; i < copies; i++)
            {
                foreach (var pageCommand in commands)
                {
                    await _engine.PrintRawAsync(pageCommand);
                    await Task.Delay(2000);
                }
                //if (copies > 1 && i < copies - 1) await Task.Delay(500);
            }
        }

        private static bool IsXps(byte[] data)
        {
            if (data.Length < 4) return false;
            return (data[0] == 'P' && data[1] == 'K') ||
                   (data[0] == '<' && data[1] == '?');
        }

        private async Task PrintXpsAsync(byte[] data)
        {
            var tmpXps = Path.Combine(Path.GetTempPath(), $"marklife_{Guid.NewGuid():N}.xps");
            try
            {
                await File.WriteAllBytesAsync(tmpXps, data);
                var settings = PrintJobSettings.FromPrintQueue(PrintQueueManager.PrinterName);
                int w = settings.PaperWidthMm > 0 ? settings.PaperWidthMm : 40;
                int h = settings.PaperHeightMm > 0 ? settings.PaperHeightMm : 30;

                var commands = XpsRenderer.RenderXpsToPrintCommands(tmpXps, w, h);
                if (commands != null)
                {
                    int copies = settings.Copies > 0 ? settings.Copies : 1;
                    for (int i = 0; i < copies; i++)
                    {
                        await _engine.PrintRawAsync(commands);
                        if (copies > 1 && i < copies - 1)
                            await Task.Delay(500);
                    }
                }
                else
                {
                    PrintJobError?.Invoke(this, "Failed to render XPS");
                }
            }
            finally
            {
                try { File.Delete(tmpXps); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _cts?.Dispose();
        }

        private static int FindSequence(byte[] data, byte[] pattern)
        {
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                    if (data[i + j] != pattern[j]) { found = false; break; }
                if (found) return i;
            }
            return -1;
        }
    }

    public class JobDetails
    {
        public string Format { get; set; } = "Unknown";
        public int WidthMm { get; set; } = 40;
        public int HeightMm { get; set; } = 30;
        public int Copies { get; set; } = 1;
        public int PageCount { get; set; } = 1;
        public string Orientation { get; set; } = "Portrait";
        public bool IsColor { get; set; } = false;
        public string Duplex { get; set; } = "None";
    }
}