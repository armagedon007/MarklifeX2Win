using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace MarklifeWin.Print
{
    public partial class PrintDialogWindow : Window
    {
        public string SelectedPaperSize { get; private set; } = "40x30";
        public int Copies { get; private set; } = 1;

        public PrintDialogWindow()
        {
            InitializeComponent();
        }

        private void CopiesBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Разрешаем только цифры
            e.Handled = !IsTextAllowed(e.Text);
        }

        private static bool IsTextAllowed(string text)
        {
            return Regex.IsMatch(text, @"^[0-9]+$");
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            // Получаем выбранный размер бумаги
            if (PaperSizeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                SelectedPaperSize = item.Tag?.ToString() ?? "40x30";
            }

            // Получаем количество копий
            if (int.TryParse(CopiesBox.Text, out int copies) && copies > 0)
            {
                Copies = copies;
            }
            else
            {
                Copies = 1;
            }

            DialogResult = true;
            Close();
        }
    }
}
