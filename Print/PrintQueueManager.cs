using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MarklifeWin.Print
{
    /// <summary>
    /// Управляет статусом принтера в Windows Spooler через Win32 API.
    /// SetPrinterOffline(true)  → значок принтера становится серым/неактивным
    /// SetPrinterOffline(false) → принтер активен
    /// </summary>
    public class PrintQueueManager : IDisposable
    {
        public const string PrinterName = "X2 Print Label";
        private bool _disposed;
        private bool _isOffline;

        // ── Win32 ─────────────────────────────────────────────────────────────

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool OpenPrinter(string name, out IntPtr hPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetPrinter(IntPtr hPrinter, int Level, IntPtr pPrinter, int Command);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetPrinter(IntPtr hPrinter, int Level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

        // PRINTER_INFO_2 — нужна только для чтения/записи Attributes
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PRINTER_INFO_2
        {
            [MarshalAs(UnmanagedType.LPTStr)] public string? pServerName;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pPrinterName;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pShareName;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pPortName;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pDriverName;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pComment;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pLocation;
            public IntPtr pDevMode;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pSepFile;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pPrintProcessor;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pDatatype;
            [MarshalAs(UnmanagedType.LPTStr)] public string? pParameters;
            public IntPtr pSecurityDescriptor;
            public uint   Attributes;
            public uint   Priority;
            public uint   DefaultPriority;
            public uint   StartTime;
            public uint   UntilTime;
            public uint   Status;
            public uint   cJobs;
            public uint   AveragePPM;
        }

        private const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x00000200;
        private const int  PRINTER_CONTROL_PAUSE          = 1;
        private const int  PRINTER_CONTROL_RESUME         = 2;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Устанавливает принтер в offline/online режим.
        /// Offline → значок принтера становится серым, задания не отправляются.
        /// </summary>
        public void SetPrinterOffline(bool offline)
        {
            if (_isOffline == offline) return;

            if (!OpenPrinter(PrinterName, out IntPtr hPrinter, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1801)
                    Debug.WriteLine($"[PrintQueue] OpenPrinter failed: {err}");
                return;
            }

            try
            {
                GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out int needed);
                IntPtr buf = Marshal.AllocHGlobal(needed);
                try
                {
                    if (GetPrinter(hPrinter, 2, buf, needed, out _))
                    {
                        var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buf);

                        if (offline)
                            info.Attributes |= PRINTER_ATTRIBUTE_WORK_OFFLINE;
                        else
                            info.Attributes &= ~PRINTER_ATTRIBUTE_WORK_OFFLINE;

                        Marshal.StructureToPtr(info, buf, false);
                        bool ok = SetPrinter(hPrinter, 2, buf, 0);
                        if (ok)
                        {
                            _isOffline = offline;
                            Debug.WriteLine($"[PrintQueue] Printer {(offline ? "OFFLINE" : "ONLINE")}");
                        }
                        else
                        {
                            int err = Marshal.GetLastWin32Error();
                            Debug.WriteLine($"[PrintQueue] SetPrinter failed: {err}");
                            // Fallback: pause/resume (works without admin)
                            FallbackPauseResume(hPrinter, offline);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }

        private void FallbackPauseResume(IntPtr hPrinter, bool pause)
        {
            int cmd = pause ? PRINTER_CONTROL_PAUSE : PRINTER_CONTROL_RESUME;
            bool ok = SetPrinter(hPrinter, 0, IntPtr.Zero, cmd);
            if (ok)
            {
                _isOffline = pause;
                Debug.WriteLine($"[PrintQueue] Fallback {(pause ? "PAUSED" : "RESUMED")}");
            }
            else
            {
                Debug.WriteLine($"[PrintQueue] Fallback also failed: {Marshal.GetLastWin32Error()}");
            }
        }

        public void PausePrinter()  => ControlPrinter(PRINTER_CONTROL_PAUSE);
        public void ResumePrinter() => ControlPrinter(PRINTER_CONTROL_RESUME);

        private void ControlPrinter(int command)
        {
            if (!OpenPrinter(PrinterName, out IntPtr hPrinter, IntPtr.Zero)) return;
            try { SetPrinter(hPrinter, 0, IntPtr.Zero, command); }
            finally { ClosePrinter(hPrinter); }
        }

        public bool IsPrinterInstalled()
        {
            if (!OpenPrinter(PrinterName, out IntPtr h, IntPtr.Zero)) return false;
            ClosePrinter(h);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SetPrinterOffline(true); // при выходе — offline
        }
    }
}
