using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Capcom
{
    /// <summary>P/Invoke crudo: user32, dwmapi, uxtheme, kernel32. Cero librerías de terceros.</summary>
    internal static class Win32
    {
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX b);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        // 🚨 el struct INPUT tiene que medir 40 bytes en x64 (la unión con MOUSEINPUT manda): con 32 el cbSize
        //    no matchea y SendInput no hace absolutamente nada, en silencio.
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        public const int WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONDBLCLK = 0xA3, WM_NCHITTEST = 0x84, WM_MOUSEWHEEL = 0x020A;
        public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;
        public const int EM_SETCUEBANNER = 0x1501;
        public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;
        public const ushort VK_MENU = 0x12;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1, SM_CXSMICON = 49;
        public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        /// <summary>RAM física libre / total, en GB. Sirve para no intentar cargar un modelo que no entra.</summary>
        public static bool Memoria(out double libreGb, out double totalGb)
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref m)) { libreGb = totalGb = 0; return false; }
            libreGb = m.ullAvailPhys / 1073741824.0;
            totalGb = m.ullTotalPhys / 1073741824.0;
            return true;
        }

        public static void EsquinasRedondas(IntPtr h, bool chicas = false)
        {
            try { int v = chicas ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND; DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4); } catch { }
        }
        public static void BordeColor(IntPtr h, int bgr)
        {
            try { DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref bgr, 4); } catch { }
        }
        public static void ModoOscuro(IntPtr h)
        {
            try { int v = 1; DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4); } catch { }
        }
        public static void BarrasOscuras(IntPtr h)
        {
            try { SetWindowTheme(h, "DarkMode_Explorer", null); } catch { }
        }
        public static void ArrastrarVentana(IntPtr h)
        {
            ReleaseCapture();
            SendMessage(h, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        public static void Mover(IntPtr h, int x, int y) => SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        static void Tecla(ushort vk, bool up)
        {
            var inp = new INPUT[1];
            inp[0].type = INPUT_KEYBOARD;
            inp[0].U.ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 };
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>Trae una ventana al frente de verdad, incluso si Windows no nos quiere dar el foreground.</summary>
        public static bool TraerAlFrente(IntPtr h)
        {
            if (!IsWindow(h)) return false;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            IntPtr fg = GetForegroundWindow();
            uint hiloFg = GetWindowThreadProcessId(fg, out _);
            uint hiloYo = GetCurrentThreadId();
            bool pegado = false;
            try
            {
                if (fg != IntPtr.Zero && hiloFg != hiloYo) pegado = AttachThreadInput(hiloYo, hiloFg, true);
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally { if (pegado) AttachThreadInput(hiloYo, hiloFg, false); }
            for (int i = 0; i < 12; i++) { if (GetForegroundWindow() == h) return true; Thread.Sleep(25); }
            Tecla(VK_MENU, false); Tecla(VK_MENU, true);   // un toque de ALT desbloquea SetForegroundWindow
            SetForegroundWindow(h);
            Thread.Sleep(60);
            return GetForegroundWindow() == h;
        }
    }
}
