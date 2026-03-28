using System;
using System.IO;
using System.Threading.Tasks;

namespace MarklifeWin.Print
{
    public class PrintEngine
    {
        private readonly Bluetooth.BluetoothPrinterManager _bluetooth;

        public PrintEngine(Bluetooth.BluetoothPrinterManager bluetooth)
        {
            _bluetooth = bluetooth;
        }

        public bool IsConnected => _bluetooth.IsConnected;

        public async Task PrintFileAsync(string filePath, int width = 40, int height = 30, int density = 2, int copies = 1)
        {
            if (!_bluetooth.IsConnected)
            {
                throw new InvalidOperationException("Printer not connected");
            }

            var data = await File.ReadAllBytesAsync(filePath);
            
            for (int i = 0; i < copies; i++)
            {
                await _bluetooth.SendDataAsync(data);
            }
        }

        public async Task PrintTestPageAsync()
        {
            if (!_bluetooth.IsConnected)
            {
                throw new InvalidOperationException("Printer not connected");
            }

            // ESC/POS тест
            var commands = new byte[]
            {
                0x1B, 0x40,  // ESC @
                0x1B, 0x61, 0x01,  // ESC a 1 (center)
            };

            // Добавляем текст
            var text = System.Text.Encoding.ASCII.GetBytes("Test Print\n\n\n\n");
            var fullCommand = new byte[commands.Length + text.Length];
            Buffer.BlockCopy(commands, 0, fullCommand, 0, commands.Length);
            Buffer.BlockCopy(text, 0, fullCommand, commands.Length, text.Length);

            await _bluetooth.SendDataAsync(fullCommand);
        }
    }
}
