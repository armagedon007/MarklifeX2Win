using System;
using System.IO;
using System.Threading.Tasks;
using MarklifeWin.Bluetooth;

namespace MarklifeWin.Print
{
    public class PrintEngine
    {
        private readonly IPrinterManager _printer;
        public const string PrinterName = PrintQueueManager.PrinterName;

        public PrintEngine(IPrinterManager printer) => _printer = printer;
        public bool IsConnected => _printer.IsConnected;

        public async Task PrintFileAsync(string filePath)
        {
            if (!_printer.IsConnected) throw new InvalidOperationException("Printer not connected");
            var data = await File.ReadAllBytesAsync(filePath);
            await PrintWithSettingsAsync(data, PrintJobSettings.FromPrintQueue(PrinterName));
        }

        public async Task PrintRawAsync(byte[] data)
        {
            if (!_printer.IsConnected) throw new InvalidOperationException("Printer not connected");
            await PrintWithSettingsAsync(data, PrintJobSettings.FromPrintQueue(PrinterName));
        }

        private async Task PrintWithSettingsAsync(byte[] data, PrintJobSettings settings)
        {
            int w = settings.PaperWidthMm > 0 ? settings.PaperWidthMm : 40;
            int h = settings.PaperHeightMm > 0 ? settings.PaperHeightMm : 30;

            byte[] printData = data;
            System.Diagnostics.Debug.WriteLine($"[Print] IsMarklifeFormat={IsMarklifeFormat(data)}");

            /*if (IsMarklifeFormat(data))
            {
                // Формат X2(Bluetooth): 1D 76 30 + raw bitmap (не сжат)
                int bIdx = FindSeq(data, new byte[] { 0x1D, 0x76, 0x30 });
                if (bIdx >= 0 && bIdx + 7 < data.Length)
                {
                    int bmpWBytes = data[bIdx+4] | (data[bIdx+5] << 8);
                    int bmpH      = data[bIdx+6] | (data[bIdx+7] << 8);
                    int bmpWDots  = bmpWBytes * 8;

                    bool labelIsLandscape = w > h;
                    bool bitmapIsPortrait = bmpH > bmpWDots;

                    System.Diagnostics.Debug.WriteLine($"[Print] {bmpWDots}x{bmpH}px, landscape={labelIsLandscape}, portrait={bitmapIsPortrait}");

                    if (labelIsLandscape && bitmapIsPortrait)
                    {
                        printData = RotateRaw(data, bIdx, bmpWBytes, bmpH);
                        System.Diagnostics.Debug.WriteLine($"[Print] Rotated: {printData.Length}b");
                    }
                }
            }*/

            await _printer.SendDataAsync(printData);
        }

        /// <summary>
        /// Поворот raw bitmap на 90° CW без сжатия.
        /// Хвост (1F 12 20 00 ...) сохраняется.
        /// </summary>
        private static byte[] RotateRaw(byte[] data, int bitmapCmdOffset,
            int srcWidthBytes, int srcHeight)
        {
            int srcWidthDots = srcWidthBytes * 8;
            int dstWidthBytes = (srcHeight + 7) / 8;
            int dstHeight = srcWidthDots;

            // Raw bitmap после 1D 76 30 00 WL WH HL HH (8 байт)
            int dataOffset = bitmapCmdOffset + 8;
            int srcBitmapSize = srcWidthBytes * srcHeight;
            var srcBitmap = new byte[srcBitmapSize];
            Array.Copy(data, dataOffset, srcBitmap, 0, Math.Min(srcBitmapSize, data.Length - dataOffset));

            // Поворачиваем на 90° по часовой
            var dstBitmap = new byte[dstWidthBytes * dstHeight];
            for (int sy = 0; sy < srcHeight; sy++)
            {
                for (int sx = 0; sx < srcWidthDots; sx++)
                {
                    int sByteIdx = sy * srcWidthBytes + sx / 8;
                    if (sByteIdx >= srcBitmap.Length) continue;
                    bool bit = (srcBitmap[sByteIdx] & (0x80 >> (sx % 8))) != 0;
                    if (!bit) continue;

                    int dx = srcHeight - 1 - sy;
                    int dy = sx;
                    int dByteIdx = dy * dstWidthBytes + dx / 8;
                    if (dByteIdx < dstBitmap.Length)
                        dstBitmap[dByteIdx] |= (byte)(0x80 >> (dx % 8));
                }
            }

            // Собираем пакет
            using var result = new MemoryStream();
            result.Write(data, 0, bitmapCmdOffset);
            result.Write(new byte[] { 0x1D, 0x76, 0x30, 0x00 });
            result.WriteByte((byte)(dstWidthBytes & 0xFF));
            result.WriteByte((byte)(dstWidthBytes >> 8));
            result.WriteByte((byte)(dstHeight & 0xFF));
            result.WriteByte((byte)(dstHeight >> 8));
            result.Write(dstBitmap);
            // Хвост (1F 12 20 00 — подача ленты и т.д.)
            int tailOffset = dataOffset + srcBitmapSize;
            if (tailOffset < data.Length)
                result.Write(data, tailOffset, data.Length - tailOffset);

            return result.ToArray();
        }

        private static int FindSeq(byte[] data, byte[] pattern)
        {
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length; j++)
                    if (data[i + j] != pattern[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        private static bool IsMarklifeFormat(byte[] data) =>
            data.Length > 4 && data[0] == 0x10 && data[1] == 0xFF;
    }
}
