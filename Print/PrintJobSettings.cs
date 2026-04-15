using System.Diagnostics;

namespace MarklifeWin.Print
{
    public class PrintJobSettings
    {
        public int PaperWidthMm { get; set; } = 40;
        public int PaperHeightMm { get; set; } = 30;
        public int Copies { get; set; } = 1;
        public int DensityDpi { get; set; } = 203;

        public static PrintJobSettings Default => new();

        /// <summary>Читает настройки из активного задания PrintQueue.</summary>
        public static PrintJobSettings FromPrintQueue(string printerName)
        {
            var settings = Default;
            try
            {
                using var server = new System.Printing.LocalPrintServer();
                using var queue = server.GetPrintQueue(printerName);

                var tiket = queue.UserPrintTicket;

                // Сам PrintTicket содержит большинство настроек
                //System.Diagnostics.Debug.WriteLine(System.Text.Json.JsonSerializer.Serialize(tiket, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

               // Debug.WriteLine(ticket.ToString());
               /*var tiket = null;

                // Размер страницы
                if (ticket.PageMediaSize != null)
                {
                    var w = ticket.PageMediaSize.Width;
                    var h = ticket.PageMediaSize.Height;
                    if (w.HasValue && h.HasValue)
                    {
                        // PageMediaSize в 1/100 дюйма → мм: * 25.4 / 100
                        settings.PaperWidthMm = (int)(w.Value * 25.4 / 9600); // WPF units = 1/96 inch
                        settings.PaperHeightMm = (int)(h.Value * 25.4 / 9600);
                    }
                }

                // Копии
                if (ticket.CopyCount.HasValue && ticket.CopyCount.Value > 0)
                    settings.Copies = ticket.CopyCount.Value;*/
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[PrintSettings] Error reading queue: {ex.Message}");
            }
            return settings;
        }
    }
}
