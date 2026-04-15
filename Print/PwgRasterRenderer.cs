using System;
using System.IO;
using System.Text;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Парсит PWG Raster (RFC 7903) и конвертирует в ESC/POS bitmap.
    /// Microsoft IPP Class Driver отправляет данные в этом формате.
    /// </summary>
    public static class PwgRasterRenderer
    {
        public static bool IsPwgRaster(byte[] data)
        {
            if (data.Length < 4) return false;
            // PWG-Raster magic: "RaS2"
            return data[0] == 'R' && data[1] == 'a' && data[2] == 'S' && data[3] == '2';
        }

        /// <summary>Конвертирует PWG Raster в ESC/POS GS v 0.</summary>
        public static byte[]? ConvertToEscPos(byte[] data)
        {
            try
            {
                using var ms  = new MemoryStream(data);
                using var br  = new BinaryReader(ms);

                // Skip magic "RaS2" (4 bytes)
                ms.Seek(4, SeekOrigin.Begin);

                // Read page header (1796 bytes per PWG spec)
                var header = ReadPageHeader(br);
                if (header == null) return null;

                System.Diagnostics.Debug.WriteLine(
                    $"[PWG] Page: {header.Width}x{header.Height}px, " +
                    $"bpp={header.BitsPerPixel}, colors={header.ColorSpace}");

                // Read pixel data
                var pixels = ReadPixelData(br, header);
                if (pixels == null) return null;

                // Convert to 1-bit monochrome ESC/POS
                return BuildEscPos(pixels, header.Width, header.Height, header.BitsPerPixel);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PWG] Parse error: {ex.Message}");
                return null;
            }
        }

        private static PwgPageHeader? ReadPageHeader(BinaryReader br)
        {
            try
            {
                var h = new PwgPageHeader();
                // PWG Raster page header is 1796 bytes
                // Key fields at specific offsets
                var headerBytes = br.ReadBytes(1796);
                if (headerBytes.Length < 1796) return null;

                // Width at offset 284 (big-endian uint32)
                h.Width  = (int)ReadBE32(headerBytes, 284);
                // Height at offset 288
                h.Height = (int)ReadBE32(headerBytes, 288);
                // BitsPerPixel at offset 264
                h.BitsPerPixel = (int)ReadBE32(headerBytes, 264);
                // ColorSpace at offset 268
                h.ColorSpace = (int)ReadBE32(headerBytes, 268);

                if (h.Width == 0 || h.Height == 0) return null;
                return h;
            }
            catch { return null; }
        }

        private static byte[]? ReadPixelData(BinaryReader br, PwgPageHeader h)
        {
            int bytesPerPixel = Math.Max(1, h.BitsPerPixel / 8);
            int rowBytes = h.Width * bytesPerPixel;
            var pixels = new byte[h.Width * h.Height * bytesPerPixel];
            int offset = 0;

            // PWG uses PackBits compression per row
            for (int y = 0; y < h.Height; y++)
            {
                int x = 0;
                while (x < rowBytes)
                {
                    if (br.BaseStream.Position >= br.BaseStream.Length) break;
                    int count = br.ReadByte();
                    if (count < 128)
                    {
                        // Literal: count+1 bytes follow
                        int n = count + 1;
                        var buf = br.ReadBytes(n);
                        Array.Copy(buf, 0, pixels, offset + x, Math.Min(n, rowBytes - x));
                        x += n;
                    }
                    else
                    {
                        // Repeat: next byte repeated (257-count) times
                        int n = 257 - count;
                        byte val = br.ReadByte();
                        for (int i = 0; i < n && x < rowBytes; i++, x++)
                            pixels[offset + x] = val;
                    }
                }
                offset += rowBytes;
            }
            return pixels;
        }

        private static byte[] BuildEscPos(byte[] pixels, int width, int height, int bpp)
        {
            int bytesPerPixel = Math.Max(1, bpp / 8);
            int widthBytes = (width + 7) / 8;
            var bitmap = new byte[widthBytes * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int pi = (y * width + x) * bytesPerPixel;
                    if (pi >= pixels.Length) break;

                    // Brightness: average of channels
                    int brightness;
                    if (bytesPerPixel == 1)
                        brightness = pixels[pi];
                    else
                        brightness = (pixels[pi] + pixels[pi+1] + pixels[pi+2]) / 3;

                    // Dark pixel → set bit (inverted: PWG 0=black)
                    if (brightness < 128)
                    {
                        int bi = y * widthBytes + x / 8;
                        bitmap[bi] |= (byte)(0x80 >> (x % 8));
                    }
                }
            }

            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x1B, 0x40 }); // ESC @
            ms.Write(new byte[] { 0x1D, 0x76, 0x30, 0x00 }); // GS v 0
            ms.Write(new byte[] { (byte)(widthBytes & 0xFF), (byte)(widthBytes >> 8) });
            ms.Write(new byte[] { (byte)(height & 0xFF), (byte)(height >> 8) });
            ms.Write(bitmap);
            ms.WriteByte(0x0C); // FF
            return ms.ToArray();
        }

        private static uint ReadBE32(byte[] buf, int offset) =>
            (uint)((buf[offset] << 24) | (buf[offset+1] << 16) | (buf[offset+2] << 8) | buf[offset+3]);

        private class PwgPageHeader
        {
            public int Width, Height, BitsPerPixel, ColorSpace;
        }
    }
}
