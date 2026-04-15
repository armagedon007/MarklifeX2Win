using System;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Вспомогательный класс для работы с кастомными размерами бумаги в XPS печати.
    /// </summary>
    public static class PrintHelper
    {
        // Размеры в 1/100 дюйма (Width x Height)
        // 1 мм = 100/25.4 ≈ 3.937 экранных единиц
        public static readonly PaperSize[] CustomPaperSizes = new[]
        {
            new PaperSize("40x30mm", 157, 118),   // 40x30mm
            new PaperSize("50x30mm", 197, 118),   // 50x30mm  
            new PaperSize("60x40mm", 236, 157),   // 60x40mm
            new PaperSize("80x60mm", 315, 236),   // 80x60mm
            new PaperSize("100x80mm", 394, 315), // 100x80mm
        };
        
        /// <summary>
        /// Конвертирует мм в 1/100 дюйма.
        /// </summary>
        public static int MmToHundredthsInch(double mm)
        {
            return (int)(mm * 100 / 25.4);
        }
        
        /// <summary>
        /// Конвертирует 1/100 дюйма в мм.
        /// </summary>
        public static double HundredthsInchToMm(int hundredthsInch)
        {
            return hundredthsInch * 25.4 / 100;
        }
        
        /// <summary>
        /// Создает PrintTicket с кастомным размером бумаги.
        /// </summary>
        public static PrintTicket CreatePrintTicket(int widthHundredthsInch, int heightHundredthsInch)
        {
            var ticket = new PrintTicket();
            
            // Создаем кастомный размер бумаги
            var pageSize = new PageMediaSize(widthHundredthsInch, heightHundredthsInch);
            ticket.PageMediaSize = pageSize;
            
            // Ориентация - портретная
            ticket.PageOrientation = PageOrientation.Portrait;
            
            return ticket;
        }
        
        /// <summary>
        /// Создает PrintTicket из размера бумаги.
        /// </summary>
        public static PrintTicket CreatePrintTicket(PaperSize paperSize)
        {
            return CreatePrintTicket(paperSize.Width, paperSize.Height);
        }
        
        /// <summary>
        /// Получает PrintQueue для принтера.
        /// </summary>
        public static PrintQueue GetPrintQueue(string printerName)
        {
            var printServer = new PrintServer();
            return printServer.GetPrintQueue(printerName);
        }
        
        /// <summary>
        /// Печатает визуальный элемент с кастомным размером бумаги.
        /// </summary>
        public static void PrintVisual(UIElement visual, string printerName, PaperSize paperSize, string jobName = "Marklife Print")
        {
            // Используем PrintDialog для печати
            var dialog = new PrintDialog();
            dialog.PrintQueue = GetPrintQueue(printerName);
            
            // Применяем кастомный размер бумаги
            var printTicket = CreatePrintTicket(paperSize);
            dialog.PrintTicket = printTicket;
            
            // Создаем документ
            var doc = new FixedDocument();
            var page = new FixedPage
            {
                Width = paperSize.Width / 100.0 * 96,
                Height = paperSize.Height / 100.0 * 96
            };
            
            page.Children.Add(visual);
            page.Measure(new Size(page.Width, page.Height));
            page.Arrange(new Rect(0, 0, page.Width, page.Height));
            page.UpdateLayout();
            
            var pageContent = new PageContent();
            ((System.Windows.Markup.IAddChild)pageContent).AddChild(page);
            doc.Pages.Add(pageContent);
            
            // Печатаем через PrintDocument
            doc.DocumentPaginator.PageSize = new Size(page.Width, page.Height);
            dialog.PrintDocument(doc.DocumentPaginator, jobName);
        }
        
        /// <summary>
        /// Применяет PrintTicket к PrintDialog.
        /// </summary>
        public static void ApplyPrintTicket(PrintDialog dialog, PaperSize paperSize)
        {
            var ticket = CreatePrintTicket(paperSize);
            dialog.PrintTicket = ticket;
        }
    }
    
    /// <summary>
    /// Описание размера бумаги.
    /// </summary>
    public class PaperSize
    {
        public string Name { get; }
        public int Width { get; }      // 1/100 дюйма
        public int Height { get; }     // 1/100 дюйма
        
        public double WidthMm => Width * 25.4 / 100;
        public double HeightMm => Height * 25.4 / 100;
        
        public PaperSize(string name, int width, int height)
        {
            Name = name;
            Width = width;
            Height = height;
        }
        
        public override string ToString() => $"{Name} ({WidthMm:F0}x{HeightMm:F0}mm)";
    }
}
