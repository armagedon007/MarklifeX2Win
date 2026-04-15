using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Конвертирует PostScript/PDF в bitmap через Ghostscript,
    /// затем формирует ESC/POS команды для термопринтера.
    /// </summary>
    public class GhostscriptRenderer
    {
        // Стандартные пути установки Ghostscript
        private static readonly string[] GsPaths = {
            @"C:\Program Files\gs\gs10.03.1\bin\gswin64c.exe",
            @"C:\Program Files\gs\gs10.02.1\bin\gswin64c.exe",
            @"C:\Program Files\gs\gs10.01.2\bin\gswin64c.exe",
            @"C:\Program Files\gs\gs10.00.0\bin\gswin64c.exe",
            @"C:\Program Files\Ghostgum\gsview\gswin64c.exe",
        };

        public static string? FindGhostscript()
        {
            // Ищем в стандартных путях
            foreach (var path in GsPaths)
                if (File.Exists(path)) return path;

            // Ищем в Program Files
            var gsDir = @"C:\Program Files\gs";
            if (Directory.Exists(gsDir))
            {
                foreach (var dir in Directory.GetDirectories(gsDir))
                {
                    var exe = Path.Combine(dir, "bin", "gswin64c.exe");
                    if (File.Exists(exe)) return exe;
                }
            }

            // Ищем в PATH
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var p in pathEnv.Split(';'))
            {
                var exe = Path.Combine(p.Trim(), "gswin64c.exe");
                if (File.Exists(exe)) return exe;
                exe = Path.Combine(p.Trim(), "gs.exe");
                if (File.Exists(exe)) return exe;
            }

            return null;
        }

        public bool IsAvailable => FindGhostscript() != null;

        /// <summary>
        /// Конвертирует PostScript данные в ESC/POS bitmap для термопринтера.
        /// </summary>
        /// <param name="psData">PostScript данные</param>
        /// <param name="widthMm">Ширина этикетки в мм</param>
        /// <param name="heightMm">Высота этикетки в мм</param>
        /// <param name="dpi">Разрешение принтера (203 или 300)</param>
        public async Task<byte[]?> RenderToEscPos(byte[] psData, int widthMm, int heightMm, int dpi = 203)
        {
            var gs = FindGhostscript();
            if (gs == null)
            {
                Debug.WriteLine("[GS] Ghostscript not found");
                return null;
            }

            var tmpPs  = Path.GetTempFileName() + ".ps";
            var tmpPng = Path.GetTempFileName() + ".png";

            try
            {
                await File.WriteAllBytesAsync(tmpPs, psData);

                // Размер в точках
                int widthPx  = (int)Math.Round(widthMm  * dpi / 25.4);
                int heightPx = (int)Math.Round(heightMm * dpi / 25.4);

                // Запускаем Ghostscript
                var args = $"-dNOPAUSE -dBATCH -dSAFER " +
                           $"-sDEVICE=pnggray " +
                           $"-r{dpi} " +
                           $"-dDEVICEWIDTHPOINTS={widthMm * 2.835:F1} " +
                           $"-dDEVICEHEIGHTPOINTS={heightMm * 2.835:F1} " +
                           $"-dFIXEDMEDIA " +
                           $"-dFitPage " +
                           $"\"-sOutputFile={tmpPng}\" " +
                           $"\"{tmpPs}\"";

                Debug.WriteLine($"[GS] Running: {gs} {args}");

                var psi = new ProcessStartInfo(gs, args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };

                using var proc = Process.Start(psi)!;
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                Debug.WriteLine($"[GS] Exit: {proc.ExitCode}");
                if (!string.IsNullOrEmpty(stderr))
                    Debug.WriteLine($"[GS] Stderr: {stderr}");

                if (proc.ExitCode != 0 || !File.Exists(tmpPng))
                {
                    Debug.WriteLine("[GS] Render failed");
                    return null;
                }

                // Конвертируем PNG в ESC/POS
                return ConvertToEscPos(tmpPng, widthPx, heightPx);
            }
            finally
            {
                try { File.Delete(tmpPs);  } catch { }
                try { File.Delete(tmpPng); } catch { }
            }
        }

        /// <summary>
        /// Конвертирует PNG bitmap в ESC/POS GS v 0 команду.
        /// </summary>
        private static byte[] ConvertToEscPos(string pngPath, int widthPx, int heightPx)
        {
            using var bmp = new Bitmap(pngPath);

            // Масштабируем если нужно
            Bitmap target;
            if (bmp.Width != widthPx || bmp.Height != heightPx)
            {
                target = new Bitmap(widthPx, heightPx);
                using var g = Graphics.FromImage(target);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, widthPx, heightPx);
            }
            else
            {
                target = bmp;
            }

            // Ширина в байтах (каждый байт = 8 точек, выравниваем по 8)
            int widthBytes = (widthPx + 7) / 8;
            int actualWidth = widthBytes * 8;

            // Формируем bitmap данные (1 бит на точку, 1=чёрный)
            var bitmapData = new byte[widthBytes * heightPx];
            for (int y = 0; y < heightPx; y++)
            {
                for (int x = 0; x < actualWidth; x++)
                {
                    if (x >= target.Width) break;
                    var pixel = target.GetPixel(x, y);
                    // Порог: если пиксель тёмный — ставим бит
                    int brightness = (pixel.R + pixel.G + pixel.B) / 3;
                    if (brightness < 128)
                    {
                        int byteIdx = y * widthBytes + x / 8;
                        int bitIdx  = 7 - (x % 8);
                        bitmapData[byteIdx] |= (byte)(1 << bitIdx);
                    }
                }
            }

            if (!ReferenceEquals(target, bmp)) target.Dispose();

            // Собираем ESC/POS пакет
            using var ms = new MemoryStream();

            // ESC @ — инициализация
            ms.Write(new byte[] { 0x1B, 0x40 });

            // GS v 0 — печать растрового изображения
            // 1D 76 30 m xL xH yL yH [data]
            // m=0 (normal), xL/xH = ширина в байтах, yL/yH = высота в строках
            ms.Write(new byte[] { 0x1D, 0x76, 0x30, 0x00 });
            ms.Write(new byte[] { (byte)(widthBytes & 0xFF), (byte)(widthBytes >> 8) });
            ms.Write(new byte[] { (byte)(heightPx & 0xFF), (byte)(heightPx >> 8) });
            ms.Write(bitmapData);

            // FF — подача бумаги
            ms.WriteByte(0x0C);

            Debug.WriteLine($"[GS] ESC/POS: {widthPx}x{heightPx}px, {widthBytes} bytes/row, total {ms.Length} bytes");
            return ms.ToArray();
        }
    }
}
