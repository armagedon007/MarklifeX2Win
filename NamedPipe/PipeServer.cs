using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace MarklifeWin.NamedPipe
{
    public class PipeServer
    {
        private readonly string _pipeName;
        private Task? _serverTask;
        private readonly Print.PrintEngine _printEngine;
        private bool _isRunning;

        public PipeServer(string pipeName, Print.PrintEngine printEngine)
        {
            _pipeName = pipeName;
            _printEngine = printEngine;
        }

        public void Start()
        {
            _isRunning = true;
            _serverTask = Task.Run(RunServerAsync);
        }

        public void Stop()
        {
            _isRunning = false;
        }

        private async Task RunServerAsync()
        {
            while (_isRunning)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync();
                    await HandleClientAsync(server);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Pipe error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream server)
        {
            try
            {
                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server);
                
                string? line = await reader.ReadLineAsync();
                if (line == null) return;

                Console.WriteLine($"[Pipe] Command: {line}");

                if (line.StartsWith("PRINT|"))
                {
                    var parts = line.Substring(6).Split('|');
                    if (parts.Length >= 7)
                    {
                        var filePath = parts[6];
                        await _printEngine.PrintFileAsync(filePath);
                    }
                    await writer.WriteLineAsync("OK");
                }
                else if (line == "STATUS")
                {
                    var status = _printEngine.IsConnected ? "connected" : "disconnected";
                    await writer.WriteLineAsync($"STATUS|{status}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }
        }
    }
}
