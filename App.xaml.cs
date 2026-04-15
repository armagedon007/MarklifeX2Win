using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using MarklifeWin.Bluetooth;
using MarklifeWin.Print;

namespace MarklifeWin
{
    public partial class App : Application
    {
        private TaskbarIcon?          _trayIcon;
        private MainWindow?           _mainWindow;
        private IPrinterManager?      _bt;
        private Timer?                _autoScanTimer;
        private RawPrintServer?       _printServer;
        private PrintQueueManager?    _printQueue;
        private Print.XpsPrintWatcher? _xpsWatcher;

        // Tray state
        private bool   _printerConnected;
        private int    _batteryLevel;
        private string _deviceName = "";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _bt = new RfcommPrinterManager();

            // Подписываемся на события прямо в App — не зависим от MainWindow
            _bt.ConnectionStateChanged += (_, connected) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (connected)
                        UpdateTrayStatus(true, _bt.ConnectedDeviceName ?? "", _batteryLevel);
                    else
                        UpdateTrayStatus(false);
                });
            };
            _bt.BatteryLevelChanged += (_, level) =>
            {
                _batteryLevel = level;
                if (_printerConnected)
                    Dispatcher.Invoke(() => UpdateTrayStatus(true, _deviceName, level));
            };

            // Восстанавливаем настройки автоподключения
            var settings = MarklifeWin.Properties.Settings.Default;
            if (settings.AutoReconnect && !string.IsNullOrEmpty(settings.LastDeviceAddress))
            {
                _bt.LastDeviceId  = settings.LastDeviceAddress;
                _bt.AutoReconnect = true;
                Debug.WriteLine($"[App] AutoReconnect restored: {settings.LastDeviceAddress}");
            }

            // Создаём MainWindow сразу — он живёт всё время, просто скрывается
            _mainWindow = new MainWindow(_bt);

            // Управление очередью печати (offline/online)
            _printQueue = new PrintQueueManager();
            _printQueue.SetPrinterOffline(true); // при старте — offline

            // Запускаем RAW print server
            var printEngine = new PrintEngine(_bt);
            _printServer = new RawPrintServer(printEngine, _printQueue);
    
            _printServer.StatusChanged    += (_, msg) => Debug.WriteLine($"[Print] {msg}");
            _printServer.PrintJobError    += (_, msg) =>
            {
                Dispatcher.Invoke(() =>
                    _trayIcon?.ShowBalloonTip("Marklife — Print Error", msg, BalloonIcon.Error));
            };
            _printServer.Start();

            // XPS watcher — для XPS Document Writer драйвера
            _xpsWatcher = new Print.XpsPrintWatcher(printEngine);
            _xpsWatcher.StatusChanged += (_, msg) => Debug.WriteLine($"[XPS] {msg}");
            _xpsWatcher.PrintError    += (_, msg) =>
            {
                Dispatcher.Invoke(() =>
                    _trayIcon?.ShowBalloonTip("Marklife — Print Error", msg, BalloonIcon.Error));
            };
            _xpsWatcher.Start();

            // Online/Offline при изменении состояния подключения
            _bt.ConnectionStateChanged += (_, connected) =>
            {
                _printQueue.SetPrinterOffline(!connected);
            };

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "Marklife Print Service",
                Visibility  = Visibility.Visible
            };

            // Передаём TrayIcon в PrintServer для показа balloon tips
            // (уже через события выше)

            LoadTrayIcon(connected: false, battery: 0);
            BuildContextMenu();

            _trayIcon.TrayLeftMouseUp      += (_, _) => { if (_trayIcon.ContextMenu != null) _trayIcon.ContextMenu.IsOpen = true; };
            _trayIcon.TrayMouseDoubleClick += (_, _) => ShowSettings();

            // Start periodic scan
            _autoScanTimer = new Timer(async _ =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (_mainWindow != null && !_bt.IsConnected)
                        await _mainWindow.RunScanAsync();
                });
            }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
        }

        private void BuildContextMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var statusItem = new System.Windows.Controls.MenuItem
            {
                Header     = "● Нет устройства",
                IsEnabled  = true,
                Foreground = System.Windows.Media.Brushes.Red,
                Name       = "StatusItem"
            };
            statusItem.Click += (_, _) => ShowSettings();
            menu.Items.Add(statusItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var settingsItem = new System.Windows.Controls.MenuItem { Header = "Настройки принтера" };
            settingsItem.Click += (_, _) => ShowSettings();
            menu.Items.Add(settingsItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem { Header = "Выход" };
            exitItem.Click += (_, _) => Shutdown();
            menu.Items.Add(exitItem);

            _trayIcon!.ContextMenu = menu;
        }

        private void ShowSettings()
        {
            _mainWindow!.Show();
            _mainWindow.Activate();
        }

        // ── Tray icon with colored dot ────────────────────────────────────────

        public void UpdateTrayStatus(bool connected, string deviceName = "", int battery = 0)
        {
            _printerConnected = connected;
            _deviceName       = deviceName;
            if (battery > 0) _batteryLevel = battery;

            Dispatcher.Invoke(() =>
            {
                LoadTrayIcon(connected, _batteryLevel);
                UpdateStatusMenuItem(connected, deviceName, _batteryLevel);
            });
        }

        public void UpdateTrayBattery(int battery)
        {
            _batteryLevel = battery;
            Dispatcher.Invoke(() =>
            {
                LoadTrayIcon(_printerConnected, battery);
                UpdateStatusMenuItem(_printerConnected, _deviceName, battery);
            });
        }

        private void UpdateStatusMenuItem(bool connected, string deviceName, int battery)
        {
            if (_trayIcon?.ContextMenu?.Items[0] is System.Windows.Controls.MenuItem item)
            {
                if (connected)
                {
                    var batteryStr = battery > 0 ? $" ({battery}%)" : "";
                    item.Header     = $"● {deviceName}{batteryStr}";
                    item.Foreground = System.Windows.Media.Brushes.Green;
                    _trayIcon.ToolTipText = $"Marklife — {deviceName}{batteryStr}";
                }
                else
                {
                    item.Header     = "● Нет устройства";
                    item.Foreground = System.Windows.Media.Brushes.Red;
                    _trayIcon.ToolTipText = "Marklife Print Service";
                }
            }
        }
        
        private void LoadTrayIcon(bool connected, int battery)
        {
            if (_trayIcon == null) return;

            IntPtr hIcon = IntPtr.Zero;
            try
            {
                const int size = 32;
                using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g   = Graphics.FromImage(bmp);
                g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                g.Clear(Color.Transparent);

                // Базовая иконка — всегда полный размер 32x32, никогда не сжимаем
                var iconRect = new Rectangle(0, 0, size, size);
                if (File.Exists("Resources/tray.ico"))
                {
                    try
                    {
                        using var baseIcon = new Icon("Resources/tray.ico", new System.Drawing.Size(size, size));
                        g.DrawIcon(baseIcon, iconRect);
                    }
                    catch { DrawFallbackPrinter(g); }
                }
                else
                {
                    DrawFallbackPrinter(g);
                }

                // Статусный кружок — правый нижний угол, всегда на одном месте
                var dotColor = connected ? Color.FromArgb(76, 175, 80) : Color.FromArgb(244, 67, 54);
                using (var wb = new SolidBrush(Color.White))
                    g.FillEllipse(wb, 20, 20, 12, 12);
                using (var db = new SolidBrush(dotColor))
                    g.FillEllipse(db, 21, 21, 10, 10);

                // Процент заряда — только в тултипе и меню, не на иконке

                hIcon = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(hIcon).Clone();
                var oldIcon = _trayIcon.Icon;
                _trayIcon.Icon = icon;
                oldIcon?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Icon error: {ex.Message}");
                try { if (File.Exists("Resources/tray.ico")) _trayIcon.Icon = new Icon("Resources/tray.ico"); }
                catch { }
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                    DestroyIcon(hIcon);
            }
        }

        private static void DrawFallbackPrinter(Graphics g)
        {
            g.FillRectangle(Brushes.DimGray, 4, 10, 24, 14);
            g.FillRectangle(Brushes.White,   8, 14, 16,  8);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        protected override void OnExit(ExitEventArgs e)
        {
            _autoScanTimer?.Dispose();
            _printQueue?.Dispose();
            _printServer?.Dispose();
            _xpsWatcher?.Dispose();
            _bt?.Dispose();
            _trayIcon?.Dispose();
            base.OnExit(e);
        }
    }
}
