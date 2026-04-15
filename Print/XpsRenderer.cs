using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Рендерит XPS документ в 1-bit bitmap и формирует Marklife ESC команды.
    /// Аналог PrintEngine.swift convertImageToPrinterCommands.
    /// </summary>
    public static class XpsRenderer
    {
        private const double DotsPerMm = 8.0; // 203dpi ≈ 8 dots/mm

        /// <summary>
        /// Конвертирует XPS файл в Marklife print команды.
        /// </summary>
        public static byte[]? RenderXpsToPrintCommands(string xpsPath, int widthMm, int heightMm, int density = 2)
        {
            try
            {
                using var xps = new XpsDocument(xpsPath, FileAccess.Read);
                var seq = xps.GetFixedDocumentSequence();
                if (seq == null) return null;

                var paginator = seq.DocumentPaginator;
                if (paginator.PageCount == 0) return null;

                // Рендерим первую страницу
                var page = paginator.GetPage(0);

                int targetW = (int)(widthMm  * DotsPerMm);
                int targetH = (int)(heightMm * DotsPerMm);

                Debug.WriteLine($"[XPS] Page size: {page.Size}, target: {targetW}x{targetH}px");

                // Рендерим в bitmap
                var bitmap = RenderPageToBitmap(page, targetW, targetH);
                if (bitmap == null) return null;

                // Конвертируем в 1-bit и формируем команды
                var bitmapData = ConvertTo1Bit(bitmap, targetW, targetH);
                return BuildMarklifeCommands(targetW, targetH, bitmapData, density);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[XPS] Render error: {ex.Message}");
                return null;
            }
        }

        private static RenderTargetBitmap? RenderPageToBitmap(DocumentPage page, int width, int height)
        {
            try
            {
                // 96 dpi = WPF units, 203 dpi = printer
                double dpi = 203.0;
                double scaleX = width  / page.Size.Width;
                double scaleY = height / page.Size.Height;

                var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    // Белый фон
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    // Масштабируем страницу
                    dc.PushTransform(new ScaleTransform(scaleX, scaleY));
                    dc.DrawRectangle(new VisualBrush(page.Visual), null,
                        new Rect(new Point(), page.Size));
                    dc.Pop();
                }

                rtb.Render(dv);
                return rtb;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[XPS] RenderPage error: {ex.Message}");
                return null;
            }
        }

        private static byte[] ConvertTo1Bit(BitmapSource bitmap, int width, int height)
        {
            // Конвертируем в grayscale
            var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);

            int widthBytes = (width + 7) / 8;
            var result = new byte[widthBytes * height];

            var pixels = new byte[width * height];
            gray.CopyPixels(pixels, width, 0);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int brightness = pixels[y * width + x];
                    if (brightness < 128) // тёмный пиксель = точка
                    {
                        int byteIdx = y * widthBytes + x / 8;
                        result[byteIdx] |= (byte)(0x80 >> (x % 8));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Формирует Marklife команды (из PrintEngine.swift buildPrintCommands).
        /// </summary>
        private static byte[] BuildMarklifeCommands(int imageWidth, int imageHeight,
            byte[] bitmapData, int density)
        {
            byte densityValue = density switch { 1 => 3, 3 => 14, _ => 8 };

            int widthBytes = (imageWidth + 7) / 8;

            using var ms = new MemoryStream();

            // Заголовок
            ms.Write(new byte[] { 0x10, 0xFF, 0xF1, 0x02 });
            ms.Write(new byte[] { 0x1F, 0x70, 0x02, densityValue });
            ms.Write(new byte[12]); // padding
            ms.Write(new byte[] { 0x1F, 0xC0, 0x01, 0x00 });
            ms.Write(new byte[] { 0x1F, 0x11, 0x51, 0x00, 0x00 });

            // 1F 10 WH WL HH HL — размер bitmap (big-endian)
            ms.WriteByte(0x1F); ms.WriteByte(0x10);
            ms.WriteByte((byte)((widthBytes >> 8) & 0xFF));
            ms.WriteByte((byte)(widthBytes & 0xFF));
            ms.WriteByte((byte)((imageHeight >> 8) & 0xFF));
            ms.WriteByte((byte)(imageHeight & 0xFF));

            // Размер данных (4 байта big-endian)
            ms.WriteByte((byte)((bitmapData.Length >> 24) & 0xFF));
            ms.WriteByte((byte)((bitmapData.Length >> 16) & 0xFF));
            ms.WriteByte((byte)((bitmapData.Length >> 8) & 0xFF));
            ms.WriteByte((byte)(bitmapData.Length & 0xFF));

            // Bitmap данные
            ms.Write(bitmapData);

            // Хвост
            ms.Write(new byte[] { 0x1F, 0x12, 0x20, 0x00 });
            ms.Write(new byte[] { 0x1F, 0xC0, 0x01, 0x01 });
            ms.Write(new byte[] { 0x1F, 0x11, 0x50, 0x00, 0x00 });

            return ms.ToArray();
        }
    }
}
