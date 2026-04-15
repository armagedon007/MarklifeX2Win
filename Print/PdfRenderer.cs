using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;
using PdfiumViewer;
using System.Runtime.InteropServices;
using System.IO.Compression;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Рендерит PDF в Marklife print команды через PdfiumViewer.
    /// </summary>
    public static class PdfRenderer
    {
        private const double DotsPerMm = 8; // 203dpi ≈ 8 dots/mm
        private const int Dpi = 203;

        public static bool IsPdf(byte[] data) =>
            data.Length >= 4 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F';

        /// <summary>
        /// Конвертирует PDF данные в Marklife print команды.
        /// </summary>
        public static List<byte[]>? RenderToPrintCommands(byte[] pdfData, int widthMm, int heightMm, int rotate = -1, int density = 2)
        {
            try
            {
                int targetW = (int)(widthMm * DotsPerMm);
                int targetH = (int)(heightMm * DotsPerMm);

                Debug.WriteLine($"[PDF] Rendering {widthMm}x{heightMm}mm = {targetW}x{targetH}px");

                using var ms = new MemoryStream(pdfData);
                using var doc = PdfiumViewer.PdfDocument.Load(ms);

                if (doc.PageCount == 0) return null;

                // Get original PDF page size
                var pdfPageSize = doc.PageSizes[0];
                int pdfW = (int)(pdfPageSize.Width / 72.0 * 25.4);
                int pdfH = (int)(pdfPageSize.Height / 72.0 * 25.4);

                bool shouldRotate = false;

                if (rotate == -1) // Auto
                {
                    // Если PDF ширина больше высоты (альбомная ориентация)
                    // И этикетка портретная (высота больше ширины) - поворачиваем
                    bool pdfIsLandscape = pdfW < pdfH;
                    bool labelIsLandscape = widthMm > heightMm;

                    // Если ориентации не совпадают - поворачиваем
                    if (pdfIsLandscape)
                    {
                        shouldRotate = true;
                    }

                    Debug.WriteLine($"[PDF] Auto rotate: PDF={pdfW}x{pdfH}mm ({pdfW > pdfH}), Label={widthMm}x{heightMm}mm, Rotate={shouldRotate}");
                }
                else
                {
                    shouldRotate = rotate == 90 || rotate == 1;
                }

                // Дополнительное правило: если ширина больше 60мм - это длина
                // (этикетка горизонтальная, надо повернуть PDF)
                if (!shouldRotate && widthMm > 60)
                {
                    shouldRotate = true;
                }
                else if (shouldRotate && heightMm > 60)
                {
                    shouldRotate = false;
                }

                int renderW = targetW;
                int renderH = targetH;
                if (shouldRotate)
                {
                    renderW = targetH;
                    renderH = targetW;
                }

                var allCommands = new List<byte[]>();
                for (int pageNum = 0; pageNum < doc.PageCount; pageNum++)
                {
                    Debug.WriteLine($"[PDF] Processing page {pageNum + 1}/{doc.PageCount}");


                    // Render page
                    var bmp = (Bitmap)doc.Render(pageNum, targetW, targetH, Dpi, Dpi, false);
                    if (shouldRotate)
                    {
                        //doc.RotatePage(pageNum, PdfRotation.Rotate90);
                        bmp = RotateBitmap90(bmp);
                        Debug.WriteLine($"[PDF] Rotating 90°, swapping {targetW}x{targetH} -> {renderW}x{renderH}");
                    }

                    bmp = ConvertTo1Bpp(bmp);

                    // Build Marklife commands
                    var pageCommand = BuildMarklifeCommands(bmp.Width, bmp.Height, bmp, density, 1);

                    if (pageCommand != null && pageCommand.Length > 0)
                    {
                        allCommands.Add(pageCommand);
                    }
                }

                if (allCommands.Count == 0)
                {
                    Debug.WriteLine($"[PDF] No commands generated");
                    return null;
                }

                Debug.WriteLine($"[PDF] Total commands: {allCommands.Count}");
                return allCommands;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PDF] Render error: {ex.Message}");
                return null;
            }
        }

        public static Bitmap ConvertTo1Bpp(Bitmap source)
        {
            int width = source.Width;
            int height = source.Height;

            // Создаём 1-битный битмап
            Bitmap result = new Bitmap(width, height, PixelFormat.Format1bppIndexed);

            // Блокируем биты для быстрого доступа
            BitmapData data = result.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format1bppIndexed);

            int widthBytes = (width + 7) / 8; // сколько байт на строку
            byte[] scan = new byte[widthBytes];

            for (int y = 0; y < height; y++)
            {
                // Очищаем буфер строки
                Array.Clear(scan, 0, scan.Length);

                for (int x = 0; x < width; x++)
                {
                    Color pixel = source.GetPixel(x, y);
                    // Яркость < 128 = белый (1), иначе чёрный (0)
                    if (pixel.GetBrightness() > 0.5)
                    {
                        scan[x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }

                // Копируем строку в битмап
                IntPtr ptr = (IntPtr)((long)data.Scan0 + y * data.Stride);
                Marshal.Copy(scan, 0, ptr, scan.Length);
            }

            result.UnlockBits(data);
            return result;
        }
        public static void SaveRawToBmp(byte[] bitmapData, int width, int height, string filePath)
        {
            int widthBytes = (width + 7) / 8;

            using var bmp = new Bitmap(width, height, PixelFormat.Format1bppIndexed);

            ColorPalette palette = bmp.Palette;
            palette.Entries[0] = Color.White;
            palette.Entries[1] = Color.Black;
            bmp.Palette = palette;

            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);

            Marshal.Copy(bitmapData, 0, bmpData.Scan0, bitmapData.Length);

            bmp.UnlockBits(bmpData);
            bmp.Save(filePath, ImageFormat.Bmp);
        }

        private static System.Drawing.Bitmap RotateBitmap90(Bitmap original)
        {
            int originalWidth = original.Width;
            int originalHeight = original.Height;

            // Создаём новый битмап с поменянными местами шириной и высотой
            var rotated = new Bitmap(originalHeight, originalWidth);

            using (var g = Graphics.FromImage(rotated))
            {
                g.Clear(Color.White);

                // Устанавливаем высокое качество
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                // Поворачиваем относительно центра
                g.TranslateTransform(originalHeight / 2f, 0);
                g.RotateTransform(90);
                g.TranslateTransform(0, -originalHeight / 2f);

                // Рисуем изображение
                g.DrawImage(original, 0, 0, originalWidth, originalHeight);
            }

            return rotated;
        }

        private static byte[] ConvertTo1Bit(Bitmap image)
        {
            int width = image.Width;
            int height = image.Height;
            int widthBytes = (width + 7) / 8;
            byte[] result = new byte[widthBytes * height];

            int tempRow = 0;
            int tempWidth = 0;
            int bitIdx = 8;
            int x = 0;
            int byteIdx = 0;
            while (x < height)
            {
                int y = 0;
                while (y < widthBytes)
                {
                    int n = 0;
                    byte dataByte = 0;
                    while (n < bitIdx)
                    {
                        int pixelX = (y * 8) + n;
                        if (pixelX < width)
                        {
                            Color pixelColor = image.GetPixel(pixelX, x);
                            tempRow = x;
                            tempWidth = width;

                            int r = pixelColor.R;
                            int g = pixelColor.G;
                            int b = pixelColor.B;

                            int brightness = (int)(r * 0.299 + g * 0.587 + b * 0.114);

                            if (brightness < 128)
                            {
                                dataByte |= (byte)(128 >> n);
                            }
                        }
                        else
                        {
                            tempRow = x;
                            tempWidth = width;
                        }
                        n++;
                        width = tempWidth;
                        x = tempRow;
                        bitIdx = 8;
                    }
                    result[byteIdx] = dataByte;
                    y++;
                    byteIdx++;
                    bitIdx = 8;
                }
                x++;
                bitIdx = 8;
            }


            /*for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < widthBytes; x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    // Яркость < 128 = черный (1)
                    if (pixel.GetBrightness() < 0.5)
                    {
                        int byteIdx = y * widthBytes + (x / 8);
                        int bitIdx = 7 - (x % 8);
                        result[byteIdx] |= (byte)(1 << bitIdx);
                    }
                }
            }*/

            return result;
        }

        public static byte[] CompressBitmapDataZlib(byte[] data)
        {
            int sourceSize = data.Length;
            int destSize = sourceSize + sourceSize / 1000 + 12;
            byte[] destBuffer = new byte[destSize];

            int compressedSize = ZlibCompressRaw(data, sourceSize, destBuffer, destSize);

            if (compressedSize > 0)
            {
                byte[] result = new byte[compressedSize];
                Array.Copy(destBuffer, 0, result, 0, compressedSize);
                return result;
            }

            return null;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ZStream
        {
            public IntPtr next_in;
            public int avail_in;
            public int total_in;
            public IntPtr next_out;
            public int avail_out;
            public int total_out;
            public byte[] msg;
            public IntPtr state;
            public IntPtr zalloc;
            public IntPtr zfree;
            public IntPtr opaque;
            public int data_type;
            public int adler;
            public int reserved;
        }

        [DllImport("zlib.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int deflateInit2_(ref ZStream stream, int level, int method, int windowBits, int memLevel, int strategy, byte[] version, int stream_size);

        [DllImport("zlib.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int deflate(ref ZStream stream, int flush);

        [DllImport("zlib.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int deflateEnd(ref ZStream stream);

        private static int ZlibCompressRaw(byte[] source, int sourceLen, byte[] dest, int destLen)
        {
            var stream = new ZStream();
            byte[] version = System.Text.Encoding.ASCII.GetBytes("1.2.3.5");

            int result = deflateInit2_(ref stream, 6, 8, 10, 8, 0, version, Marshal.SizeOf(stream));

            if (result != 0)
                return 0;

            GCHandle srcHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
            GCHandle dstHandle = GCHandle.Alloc(dest, GCHandleType.Pinned);

            stream.next_in = srcHandle.AddrOfPinnedObject();
            stream.avail_in = sourceLen;
            stream.next_out = dstHandle.AddrOfPinnedObject();
            stream.avail_out = destLen;

            result = deflate(ref stream, 4);

            int compressedSize = destLen - stream.avail_out;

            deflateEnd(ref stream);

            srcHandle.Free();
            dstHandle.Free();

            return (result == 0 || result == 1) ? compressedSize : 0;
        }
        public static byte[] CompressBitmapData(byte[] imageData)
        {
            using var outputStream = new MemoryStream();
            using (var deflateStream = new DeflateStream(outputStream, CompressionLevel.Optimal))
            {
                deflateStream.Write(imageData, 0, imageData.Length);
            }

            return outputStream.ToArray();
        }

        /// <summary>Формирует Marklife команды (из PrintEngine.swift buildPrintCommands).</summary>
        private static byte[] BuildMarklifeCommands(int imageWidth, int imageHeight,
            Bitmap image, int density, int type = 1)
        {
            /*string hexString = File.ReadAllText("C:\\Users\\chai9\\Downloads\\MarklifeX2Win-main\\MarklifeX2Win-main\\command_print_mac.txt").Trim();
            hexString = hexString.Replace("\r", "").Replace("\n", "").Replace(" ", "");
            byte[] bytes = new byte[hexString.Length / 2];

            for (int i = 0; i < hexString.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hexString.Substring(i, 2), 16);
            }

            using var ms = new MemoryStream(bytes);*/

            byte densityValue = density switch { 1 => 3, 3 => 14, _ => 8 };
            int widthBytes = (imageWidth + 7) / 8;

            using var ms = new MemoryStream();


            // /return ms.ToArray();

            if (type == 2)
            {
                byte[] imageData;
                using (var msBitmap = new MemoryStream())
                {
                    image.Save(msBitmap, ImageFormat.Bmp);
                    imageData = msBitmap.ToArray();
                }
                var compressedData = CompressBitmapDataZlib(imageData);
                // Плотность
                ms.Write(new byte[] { 0x1F, 0x70, 0x02, densityValue });
                ms.Write(new byte[12]);
                ms.Write(new byte[] { 0x1F, 0xC0, 0x01, 0x00 });
                // Начало печати
                ms.Write(new byte[] { 0x1F, 0x11, 0x51, 0x00, 0x00 });

                ms.Write(new byte[] { 0x1F, 0x10,
                    (byte)((widthBytes >> 8) & 0xFF), (byte)(widthBytes & 0xFF),
                    (byte)((imageHeight >> 8) & 0xFF), (byte)(imageHeight & 0xFF) });

                ms.WriteByte((byte)((compressedData.Length >> 24) & 0xFF));
                ms.WriteByte((byte)((compressedData.Length >> 16) & 0xFF));
                ms.WriteByte((byte)((compressedData.Length >> 8) & 0xFF));
                ms.WriteByte((byte)(compressedData.Length & 0xFF));

                ms.Write(compressedData);

                ms.Write(new byte[] { 0x1F, 0x12, 0x20, 0x00 });
                ms.Write(new byte[] { 0x1F, 0xC0, 0x01, 0x01 });
                ms.Write(new byte[] { 0x1F, 0x11, 0x50, 0x00, 0x00 });
            }
            else
            {
                var bitmapData = ConvertTo1Bit(image);

                //начало печати
                ms.Write(new byte[] { 0x10, 0xFF, 0x40, 0x86 });

                ms.Write(new byte[] { 0x1F, 0x80, 0x02, 0x20 });

                // Плотность
                ms.Write(new byte[] { 0x1F, 0x70, 0x02, densityValue });

                // Начало печати
                ms.Write(new byte[] { 0x1F, 0xC0, 0x01, 0x00 });

                //adjustPositionAuto(81)
                ms.Write(new byte[] { 0x1F, 0x11, 0x51 });

                // Команда растрового изображения
                ms.Write(new byte[] { 0x1D, 0x76, 0x30, 0x00 });

                // Raw bitmap после 1D 76 30 00 WL WH HL HH (8 байт)



                // Ширина в байтах (little-endian)
                ms.WriteByte((byte)(widthBytes & 0xFF));
                ms.WriteByte((byte)((widthBytes >> 8) & 0xFF));

                // Высота (little-endian)
                ms.WriteByte((byte)(imageHeight & 0xFF));
                ms.WriteByte((byte)((imageHeight >> 8) & 0xFF));

                // Отступы (4 байта: левый, верхний, правый, нижний?) - оригинал ставит 0
                //ms.Write(new byte[12]);
                //ms.Write(new byte[] { 0x00, 0x00 });

                // Размер данных (4 байта big-endian)

                /*ms.WriteByte((byte)(bitmapData.Length & 0xFF));
                ms.WriteByte((byte)((bitmapData.Length >> 8) & 0xFF));
                ms.WriteByte((byte)((bitmapData.Length >> 16) & 0xFF));
                ms.WriteByte((byte)((bitmapData.Length >> 24) & 0xFF));*/

                ms.Write(bitmapData);

                ms.Write(new byte[] { 0x1F, 0x12, 0x20, 0x00 });

                //конец печати
                ms.Write(new byte[] { 0x1F, 0xC0, 0x01, 0x01 });

                //adjustPositionAuto(80)
                ms.Write(new byte[] { 0x1F, 0x11, 0x50, 0x86 });

                ms.Write(new byte[] { 0xFF, 0xF1, 0x45 });
            }

            return ms.ToArray();
        }
    }
}
