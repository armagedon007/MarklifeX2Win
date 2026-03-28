using System;
using System.IO;
using System.Threading;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace MarklifeWin
{
    public partial class App : Application
    {
        private TaskbarIcon? _trayIcon;
        private MainWindow? _mainWindow;
        private Timer? _autoScanTimer;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Создаём иконку в трее
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "Marklife Print Service",
                Visibility = Visibility.Visible
            };
            
            // Пытаемся загрузить иконку, если не получится - используем стандартную
            try
            {
                if (File.Exists("Resources/tray.ico"))
                {
                    _trayIcon.Icon = new System.Drawing.Icon("Resources/tray.ico");
                }
            }
            catch
            {
                // Используем стандартную иконку приложения
            }

            // Создаём контекстное меню
            var contextMenu = new System.Windows.Controls.ContextMenu();
            
            // Пункт статуса (активный)
            var statusMenuItem = new System.Windows.Controls.MenuItem
            {
                Header = "● Нет устройства",
                IsEnabled = true,
                Foreground = System.Windows.Media.Brushes.Red
            };
            statusMenuItem.Click += (s, args) => ShowSettings();
            contextMenu.Items.Add(statusMenuItem);
            
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            
            // Пункт настроек
            var settingsMenuItem = new System.Windows.Controls.MenuItem
            {
                Header = "Настройки принтера"
            };
            settingsMenuItem.Click += (s, args) => ShowSettings();
            contextMenu.Items.Add(settingsMenuItem);
            
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            
            // Пункт выхода
            var exitMenuItem = new System.Windows.Controls.MenuItem
            {
                Header = "Выход"
            };
            exitMenuItem.Click += (s, args) => Shutdown();
            contextMenu.Items.Add(exitMenuItem);

            _trayIcon.ContextMenu = contextMenu;
            
            // Клик левой кнопкой - показать меню
            _trayIcon.TrayLeftMouseUp += (s, args) => 
            {
                if (_trayIcon.ContextMenu != null)
                {
                    _trayIcon.ContextMenu.IsOpen = true;
                }
            };
            
            // Двойной клик - открыть настройки
            _trayIcon.TrayMouseDoubleClick += (s, args) => ShowSettings();

            // Обработка закрытия окна - сворачиваем в трей
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Запускаем автоматический поиск при запуске
            StartAutoScan();
        }

        private void StartAutoScan()
        {
            // Запускаем периодическое сканирование каждые 30 секунд
            _autoScanTimer = new Timer(async _ =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (_mainWindow != null)
                    {
                        await _mainWindow.ScanAsync();
                    }
                });
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        }

        private void ShowSettings()
        {
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow();
                _mainWindow.Closed += (s, args) => _mainWindow = null;
            }
            
            _mainWindow.Show();
            _mainWindow.Activate();
        }

        public void UpdateTrayStatus(bool connected, string deviceName = "", int battery = 0)
        {
            if (_trayIcon == null) return;

            var contextMenu = _trayIcon.ContextMenu;
            if (contextMenu?.Items.Count > 0 && contextMenu.Items[0] is System.Windows.Controls.MenuItem statusMenuItem)
            {
                if (connected)
                {
                    statusMenuItem.Header = $"● {deviceName} - ({battery}%)";
                    statusMenuItem.Foreground = System.Windows.Media.Brushes.Green;
                    _trayIcon.ToolTipText = $"Marklife Print Service - {deviceName} ({battery}%)";
                }
                else
                {
                    statusMenuItem.Header = "● Нет устройства";
                    statusMenuItem.Foreground = System.Windows.Media.Brushes.Red;
                    _trayIcon.ToolTipText = "Marklife Print Service";
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _autoScanTimer?.Dispose();
            _trayIcon?.Dispose();
            base.OnExit(e);
        }
    }
}