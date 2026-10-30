// Helligkeit: steuert die Helligkeit externer Monitore per DDC/CI.
// Ohne Argumente: Symbol neben der Uhr (Linksklick = Regler, Rechtsklick = Menü).
// CLI:  Helligkeit.exe list | <nr|all> <0-100|+n|-n>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint value);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

class Monitor
{
    public string Name;
    public IntPtr Handle;
    public int Left;
    public uint Min, Max = 100;
    public int Percent;
    public bool Supported;
    readonly object gate = new object();

    public void Read()
    {
        uint min, cur, max;
        lock (gate)
        {
            for (int i = 0; i < 3; i++) // DDC/CI antwortet manchmal erst beim zweiten Versuch
            {
                if (Native.GetMonitorBrightness(Handle, out min, out cur, out max) && max > min)
                {
                    Min = min; Max = max; Supported = true;
                    Percent = (int)Math.Round((cur - min) * 100.0 / (max - min));
                    return;
                }
                Thread.Sleep(50);
            }
        }
    }

    public bool Set(int percent)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        uint raw = Min + (uint)Math.Round((Max - Min) * percent / 100.0);
        lock (gate)
        {
            for (int i = 0; i < 3; i++)
            {
                if (Native.SetMonitorBrightness(Handle, raw)) { Percent = percent; return true; }
                Thread.Sleep(50);
            }
        }
        return false;
    }
}

static class Monitors
{
    static readonly List<Native.PHYSICAL_MONITOR[]> allocated = new List<Native.PHYSICAL_MONITOR[]>();

    public static List<Monitor> Load()
    {
        Dictionary<string, string> names = FriendlyNames();
        List<Monitor> result = new List<Monitor>();

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr d)
        {
            Native.MONITORINFOEX info = new Native.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
            Native.GetMonitorInfo(hMon, ref info);

            uint count;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0) return true;
            Native.PHYSICAL_MONITOR[] phys = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hMon, count, phys)) return true;
            allocated.Add(phys);

            string name = LookupName(info.szDevice, names);
            for (int i = 0; i < count; i++)
            {
                Monitor m = new Monitor();
                m.Handle = phys[i].hPhysicalMonitor;
                m.Left = info.rcMonitor.Left;
                m.Name = name ?? phys[i].szPhysicalMonitorDescription;
                result.Add(m);
            }
            return true;
        }, IntPtr.Zero);

        result.Sort(delegate (Monitor a, Monitor b) { return a.Left.CompareTo(b.Left); }); // links nach rechts
        foreach (Monitor m in result) m.Read();
        return result;
    }

    public static void Release()
    {
        foreach (Native.PHYSICAL_MONITOR[] p in allocated) Native.DestroyPhysicalMonitors((uint)p.Length, p);
        allocated.Clear();
    }

    // Ordnet \\.\DISPLAYn dem Modellnamen aus den EDID-Daten (WmiMonitorID) zu.
    static string LookupName(string adapter, Dictionary<string, string> names)
    {
        Native.DISPLAY_DEVICE dd = new Native.DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
        // Flag 1 = EDD_GET_DEVICE_INTERFACE_NAME -> \\?\DISPLAY#VSCD73A#5&...&UID24834#{guid}
        if (!Native.EnumDisplayDevices(adapter, 0, ref dd, 1)) return null;
        string[] parts = dd.DeviceID.Split('#');
        if (parts.Length < 3) return null;
        string key = ("DISPLAY\\" + parts[1] + "\\" + parts[2]).ToUpperInvariant();
        foreach (KeyValuePair<string, string> kv in names)
            if (kv.Key.StartsWith(key)) return kv.Value;
        return null;
    }

    static Dictionary<string, string> FriendlyNames()
    {
        Dictionary<string, string> map = new Dictionary<string, string>();
        try
        {
            using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorID"))
                foreach (ManagementObject o in s.Get())
                {
                    string maker = Decode(o["ManufacturerName"] as ushort[]);
                    string model = Decode(o["UserFriendlyName"] as ushort[]);
                    map[((string)o["InstanceName"]).ToUpperInvariant()] = (maker + " " + model).Trim();
                }
        }
        catch { }
        return map;
    }

    static string Decode(ushort[] data)
    {
        if (data == null) return "";
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (ushort c in data) if (c != 0) sb.Append((char)c);
        return sb.ToString();
    }
}

static class Theme
{
    // Personalize: AppsUseLightTheme (Fenster), SystemUsesLightTheme (Taskleiste)
    public static bool Light(string value)
    {
        try
        {
            object v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", value, 1);
            return v == null || (int)v != 0;
        }
        catch { return true; }
    }

    // Windows-Akzentfarbe in der Variante, die Windows selbst für Regler nutzt
    // (AccentPalette: 0-2 hell, 3 Basis, 4-6 dunkel; dunkles Design -> hell, helles Design -> dunkel).
    public static Color Accent(bool lightTheme)
    {
        try
        {
            byte[] p = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) as byte[];
            if (p != null && p.Length >= 32)
            {
                int i = lightTheme ? 4 : 1;
                return Color.FromArgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            }
        }
        catch { }
        return lightTheme ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255);
    }

    // Sonnen-Symbol für den Infobereich, passend zur Taskleistenfarbe.
    public static Icon SunIcon()
    {
        Color c = Light("SystemUsesLightTheme") ? Color.FromArgb(32, 32, 32) : Color.White;
        using (Bitmap b = new Bitmap(32, 32))
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen p = new Pen(c, 3f))
            {
                p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.DrawLine(p, 16 + (float)(Math.Cos(a) * 10.5), 16 + (float)(Math.Sin(a) * 10.5),
                                  16 + (float)(Math.Cos(a) * 14.5), 16 + (float)(Math.Sin(a) * 14.5));
                }
            }
            using (SolidBrush br = new SolidBrush(c)) g.FillEllipse(br, 9.5f, 9.5f, 13f, 13f);
            return Icon.FromHandle(b.GetHicon());
        }
    }
}

// Windows-Schnittstellen für das halbdurchsichtige, selbst gezeichnete Popup.
static class Layered
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] public struct ACCENT_POLICY { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] public struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr h, ref WINCOMPATTRDATA data);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    // Fertiges Bild (mit Alphakanal) an Position pos mit Gesamt-Deckkraft alpha anzeigen.
    public static void Push(IntPtr hwnd, Bitmap bmp, Point pos, byte alpha)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        try
        {
            POINT dst = new POINT(pos.X, pos.Y), src = new POINT(0, 0);
            SIZE size = new SIZE(bmp.Width, bmp.Height);
            BLENDFUNCTION bf = new BLENDFUNCTION();
            bf.BlendOp = 0; bf.SourceConstantAlpha = alpha; bf.AlphaFormat = 1; // AC_SRC_ALPHA
            UpdateLayeredWindow(hwnd, screen, ref dst, ref size, mem, ref src, 0, ref bf, 2); // ULW_ALPHA
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Unschärfe hinter dem Fenster (wie bei Windows-Flyouts).
    public static void EnableBlur(IntPtr hwnd)
    {
        ACCENT_POLICY accent = new ACCENT_POLICY();
        accent.AccentState = 3; // ACCENT_ENABLE_BLURBEHIND
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            WINCOMPATTRDATA data = new WINCOMPATTRDATA();
            data.Attribute = 19; // WCA_ACCENT_POLICY
            data.Data = ptr;
            data.SizeOfData = size;
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}

// Aufklappfenster im Stil der Windows-11-Schnelleinstellungen:
// abgerundet, halbdurchsichtig, fliegt von unten ein. Alles selbst gezeichnet.
class BrightnessPanel : Form
{
    const int RowH = 58, Header = 46, SepH = 9, Pad = 16, Radius = 8, Slide = 40, AnimMs = 260;

    readonly List<Monitor> monitors;
    readonly int[] values;   // [0] = Alle, [i+1] = Monitor i
    readonly bool[] enabled;
    readonly System.Windows.Forms.Timer[] debounce;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    readonly bool light;
    readonly Color accent;
    readonly float s; // DPI-Faktor
    readonly int W, H;
    readonly Font fTitle, fLabel, fValue, fIcon;
    Point target, pos;
    int visibleBottom = int.MaxValue; // Unterkante des Arbeitsbereichs: darunter wird abgeschnitten
    DateTime animStart;
    int dragRow = -1, hoverRow = -1;
    public DateTime LastHidden = DateTime.MinValue;
    public event EventHandler ValuesChanged;

    public BrightnessPanel(List<Monitor> monitors)
    {
        this.monitors = monitors;
        light = Theme.Light("AppsUseLightTheme");
        accent = Theme.Accent(light);
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;

        int n = monitors.Count + 1;
        values = new int[n];
        enabled = new bool[n];
        enabled[0] = true;
        debounce = new System.Windows.Forms.Timer[monitors.Count];
        for (int i = 0; i < monitors.Count; i++)
        {
            int idx = i;
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 120; // DDC/CI ist langsam: erst senden, wenn der Regler kurz ruht
            t.Tick += delegate
            {
                t.Stop();
                int v = values[idx + 1];
                Monitor m = monitors[idx];
                ThreadPool.QueueUserWorkItem(delegate
                {
                    m.Set(v);
                    try { BeginInvoke((MethodInvoker)Changed); } catch { }
                });
            };
            debounce[i] = t;
        }

        W = S(380);
        H = S(Header + n * RowH + SepH + 8);
        fTitle = new Font(FontOr("Segoe UI Variable Display Semib", "Segoe UI Semibold"), 11f * s, GraphicsUnit.Point);
        fLabel = new Font(FontOr("Segoe UI Variable Text", "Segoe UI"), 9f * s, GraphicsUnit.Point);
        fValue = new Font(FontOr("Segoe UI Variable Text", "Segoe UI"), 9.5f * s, GraphicsUnit.Point);
        fIcon = new Font(FontOr("Segoe Fluent Icons", "Segoe MDL2 Assets"), 12f * s, GraphicsUnit.Point);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Size = new Size(W, H);

        anim.Interval = 10;
        anim.Tick += delegate { AnimStep(); };
        Deactivate += delegate { ClosePanel(); };

        SyncFromModel();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80; // WS_EX_LAYERED | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Layered.EnableBlur(Handle);
        SetClip();
    }

    int S(double v) { return (int)Math.Round(v * s); }

    static string FontOr(string wanted, string fallback)
    {
        foreach (FontFamily f in FontFamily.Families) if (f.Name == wanted) return wanted;
        return fallback;
    }

    // Abgerundete Fensterform; während der Animation alles unterhalb der Taskleisten-Kante abschneiden.
    void SetClip()
    {
        IntPtr round = Layered.CreateRoundRectRgn(0, 0, W + 1, H + 1, S(Radius * 2), S(Radius * 2));
        int visible = Math.Max(0, Math.Min(H, visibleBottom - pos.Y));
        if (visible < H)
        {
            IntPtr cut = Layered.CreateRectRgn(0, 0, W, visible);
            Layered.CombineRgn(round, round, cut, 1); // RGN_AND
            Layered.DeleteObject(cut);
        }
        Layered.SetWindowRgn(Handle, round, true); // Windows übernimmt die Region
    }

    // ---------- Daten ----------

    public void SyncFromModel()
    {
        for (int i = 0; i < monitors.Count; i++)
        {
            enabled[i + 1] = monitors[i].Supported;
            values[i + 1] = monitors[i].Supported ? monitors[i].Percent : 0;
        }
        values[0] = Average();
        Redraw();
    }

    int Average()
    {
        int sum = 0, n = 0;
        for (int i = 1; i < values.Length; i++) if (enabled[i]) { sum += values[i]; n++; }
        return n == 0 ? 50 : (int)Math.Round((double)sum / n);
    }

    public void RefreshFromMonitors()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            foreach (Monitor m in monitors) m.Read();
            try { BeginInvoke((MethodInvoker)delegate { if (dragRow < 0) { SyncFromModel(); Changed(); } }); } catch { }
        });
    }

    void SetRow(int row, int v)
    {
        v = Math.Max(0, Math.Min(100, v));
        if (row == 0)
        {
            values[0] = v;
            for (int k = 0; k < monitors.Count; k++)
                if (enabled[k + 1]) { values[k + 1] = v; Kick(k); }
        }
        else
        {
            values[row] = v;
            Kick(row - 1);
            values[0] = Average();
        }
        Redraw();
    }

    void Kick(int k) { debounce[k].Stop(); debounce[k].Start(); }

    void Changed() { if (ValuesChanged != null) ValuesChanged(this, EventArgs.Empty); }

    // ---------- Anzeigen / Animation ----------

    public void ShowNearTray()
    {
        Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        target = new Point(wa.Right - W - S(12), wa.Bottom - H - S(12));
        visibleBottom = wa.Bottom;
        pos = new Point(target.X, target.Y + S(Slide));
        Location = pos;
        SetClip();
        Render(0);
        Show();
        // Direkt nach ganz oben legen; TopMost allein greift nicht zuverlässig.
        Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        Native.SetForegroundWindow(Handle);
        Activate();
        animStart = DateTime.Now;
        anim.Start();
        RefreshFromMonitors();
    }

    void AnimStep()
    {
        double t = Math.Min(1.0, (DateTime.Now - animStart).TotalMilliseconds / AnimMs);
        double eased = 1 - Math.Pow(1 - t, 3); // schnell starten, sanft abbremsen
        pos = new Point(target.X, target.Y + (int)Math.Round(S(Slide) * (1 - eased)));
        SetClip();
        Render((byte)(255 * Math.Min(1.0, t * 1.8)));
        if (t >= 1) anim.Stop();
    }

    void ClosePanel()
    {
        if (!Visible) return;
        anim.Stop();
        dragRow = -1; hoverRow = -1;
        Hide();
        LastHidden = DateTime.Now;
    }

    void Redraw() { if (IsHandleCreated && Visible && !anim.Enabled) Render(255); }

    // ---------- Zeichnen ----------

    int RowTop(int i) { return S(Header + i * RowH + (i >= 1 ? SepH : 0)); }
    int TrackX0 { get { return S(Pad + 34); } }
    int TrackX1 { get { return W - S(Pad + 54); } }
    int TrackY(int i) { return RowTop(i) + S(38); }

    void Render(byte alpha)
    {
        using (Bitmap b = Draw()) Layered.Push(Handle, b, pos, alpha);
    }

    // Zum Prüfen ohne Bildschirm: Helligkeit.exe --render datei.png
    public void SaveImage(string file) { using (Bitmap b = Draw()) b.Save(file); }

    Bitmap Draw()
    {
        Bitmap b = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Color bg = light ? Color.FromArgb(215, 243, 243, 243) : Color.FromArgb(200, 38, 38, 38);
                Color border = light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255);
                Color fg = light ? Color.FromArgb(26, 26, 26) : Color.White;

                using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(0.5f, 0.5f, W - 1, H - 1, S(Radius)))
                {
                    using (SolidBrush br = new SolidBrush(bg)) g.FillPath(br, p);
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
                }

                using (SolidBrush br = new SolidBrush(fg))
                    g.DrawString("Helligkeit", fTitle, br, S(Pad) - S(1), S(14));

                int sepY = S(Header + RowH + SepH / 2.0);
                using (Pen pen = new Pen(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255), 1f))
                    g.DrawLine(pen, S(Pad), sepY, W - S(Pad), sepY);

                for (int i = 0; i < values.Length; i++) DrawRow(g, i, fg);
            }
        }
        return b;
    }

    void DrawRow(Graphics g, int i, Color fg)
    {
        bool en = enabled[i];
        Color fg2 = Color.FromArgb(en ? (light ? 165 : 200) : 110, fg);
        Color rail = light ? Color.FromArgb(en ? 115 : 50, 0, 0, 0) : Color.FromArgb(en ? 140 : 55, 255, 255, 255);
        int top = RowTop(i);
        float cy = TrackY(i);
        int x0 = TrackX0, x1 = TrackX1;

        string label = i == 0 ? "Alle Monitore" : i + "   " + monitors[i - 1].Name;
        using (SolidBrush br = new SolidBrush(fg2)) g.DrawString(label, fLabel, br, S(Pad) - S(1), top + S(6));

        StringFormat center = new StringFormat();
        center.Alignment = StringAlignment.Center;
        center.LineAlignment = StringAlignment.Center;
        using (SolidBrush br = new SolidBrush(en ? fg : fg2))
            g.DrawString(i == 0 ? "" : "", fIcon, br, new RectangleF(S(Pad) - S(2), cy - S(13), S(26), S(26)), center);

        // Schiene + gefüllter Teil in Akzentfarbe
        float th = S(4);
        float xv = x0 + (x1 - x0) * values[i] / 100f;
        using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(x0, cy - th / 2, x1 - x0, th, th / 2))
        using (SolidBrush br = new SolidBrush(rail)) g.FillPath(br, p);
        if (en && xv > x0 + 1)
            using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(x0, cy - th / 2, xv - x0, th, th / 2))
            using (SolidBrush br = new SolidBrush(accent)) g.FillPath(br, p);

        // Knopf: äußerer Ring, innerer Punkt wächst bei Hover, schrumpft beim Ziehen
        float ro = S(10);
        float ri = dragRow == i ? S(5) : hoverRow == i && en ? S(7.5) : S(6);
        using (SolidBrush br = new SolidBrush(light ? Color.White : Color.FromArgb(69, 69, 69)))
            g.FillEllipse(br, xv - ro, cy - ro, ro * 2, ro * 2);
        using (Pen pen = new Pen(light ? Color.FromArgb(35, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255), 1f))
            g.DrawEllipse(pen, xv - ro, cy - ro, ro * 2, ro * 2);
        using (SolidBrush br = new SolidBrush(en ? accent : rail))
            g.FillEllipse(br, xv - ri, cy - ri, ri * 2, ri * 2);

        StringFormat right = new StringFormat();
        right.Alignment = StringAlignment.Far;
        right.LineAlignment = StringAlignment.Center;
        using (SolidBrush br = new SolidBrush(en ? fg : fg2))
        {
            if (en)
                g.DrawString(values[i] + " %", fValue, br,
                    new RectangleF(x1 + S(8), cy - S(12), W - S(Pad) - x1 - S(8), S(24)), right);
            else // Hinweis rechts in der Namenszeile, damit er nicht umbricht
                g.DrawString("kein DDC/CI", fLabel, br,
                    new RectangleF(S(Pad), top + S(4), W - S(Pad * 2), S(22)), right);
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = Math.Min(r * 2, Math.Min(w, h));
        if (d <= 0) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- Maus / Tastatur ----------

    int HitRow(Point p, bool anyX)
    {
        for (int i = 0; i < values.Length; i++)
        {
            int top = RowTop(i);
            if (p.Y < top || p.Y >= top + S(RowH)) continue;
            if (anyX || (p.X >= TrackX0 - S(14) && p.X <= TrackX1 + S(14))) return i;
        }
        return -1;
    }

    void SetFromX(int row, int x)
    {
        SetRow(row, (int)Math.Round((x - TrackX0) * 100.0 / (TrackX1 - TrackX0)));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int r = HitRow(e.Location, false);
        if (r >= 0 && enabled[r]) { dragRow = r; Capture = true; SetFromX(r, e.X); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragRow >= 0) { SetFromX(dragRow, e.X); return; }
        int r = HitRow(e.Location, false);
        if (r != hoverRow) { hoverRow = r; Redraw(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragRow < 0) return;
        dragRow = -1;
        Capture = false;
        Redraw();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverRow >= 0 && dragRow < 0) { hoverRow = -1; Redraw(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int r = HitRow(e.Location, true);
        if (r >= 0 && enabled[r]) SetRow(r, values[r] + (e.Delta > 0 ? 2 : -2));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) ClosePanel();
    }
}

// Läuft im Infobereich: Linksklick = Regler, Rechtsklick = Menü.
class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly NotifyIcon icon = new NotifyIcon();
    readonly Control invoker = new Control(); // für Aufrufe aus anderen Threads
    readonly ToolStripMenuItem autostart;
    List<Monitor> monitors;
    BrightnessPanel panel;

    public TrayApp(bool showPanel, EventWaitHandle showSignal)
    {
        invoker.CreateControl();
        monitors = Monitors.Load();
        BuildPanel();

        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripItem open = menu.Items.Add("Helligkeit öffnen", null, delegate { panel.ShowNearTray(); });
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        foreach (int p in new int[] { 100, 75, 50, 25 })
        {
            int preset = p;
            menu.Items.Add("Alle auf " + p + " %", null, delegate { SetAll(preset); });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Monitore neu erkennen", null, delegate { Reload(); });
        autostart = new ToolStripMenuItem("Mit Windows starten", null, delegate { ToggleAutostart(); });
        autostart.Checked = AutostartEnabled();
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, delegate { Quit(); });

        icon.Icon = Theme.SunIcon();
        icon.ContextMenuStrip = menu;
        icon.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) TogglePanel(); };
        icon.Visible = true;
        UpdateTooltip();

        // Zweiter Programmstart -> vorhandenes Fenster zeigen statt neu starten.
        Thread waiter = new Thread(delegate ()
        {
            while (showSignal.WaitOne())
                invoker.BeginInvoke((MethodInvoker)delegate { panel.ShowNearTray(); });
        });
        waiter.IsBackground = true;
        waiter.Start();

        if (showPanel) panel.ShowNearTray();
    }

    void BuildPanel()
    {
        panel = new BrightnessPanel(monitors);
        panel.ValuesChanged += delegate { UpdateTooltip(); };
        IntPtr h = panel.Handle; // Fenster-Handle jetzt anlegen, damit BeginInvoke funktioniert
    }

    void TogglePanel()
    {
        if (panel.Visible) { panel.Hide(); return; }
        // Klick aufs Symbol nimmt dem offenen Fenster den Fokus -> es schließt sich gerade erst.
        if ((DateTime.Now - panel.LastHidden).TotalMilliseconds < 300) return;
        panel.ShowNearTray();
    }

    void SetAll(int percent)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            foreach (Monitor m in monitors) if (m.Supported) m.Set(percent);
            invoker.BeginInvoke((MethodInvoker)delegate { panel.SyncFromModel(); UpdateTooltip(); });
        });
    }

    void Reload()
    {
        panel.Dispose();
        Monitors.Release();
        monitors = Monitors.Load();
        BuildPanel();
        UpdateTooltip();
        panel.ShowNearTray();
    }

    void UpdateTooltip()
    {
        string text = "Helligkeit";
        for (int i = 0; i < monitors.Count; i++)
            if (monitors[i].Supported) text += "\n" + (i + 1) + ": " + monitors[i].Percent + " %";
        icon.Text = text.Length > 63 ? text.Substring(0, 63) : text; // Windows-Limit
    }

    static bool AutostartEnabled()
    {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("Helligkeit") != null;
    }

    void ToggleAutostart()
    {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (AutostartEnabled()) k.DeleteValue("Helligkeit", false);
            else k.SetValue("Helligkeit", "\"" + Application.ExecutablePath + "\" --autostart");
        }
        autostart.Checked = AutostartEnabled();
    }

    void Quit()
    {
        icon.Visible = false;
        icon.Dispose();
        Monitors.Release();
        ExitThread();
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render")
        {
            Layered.SetProcessDPIAware();
            BrightnessPanel p = new BrightnessPanel(Monitors.Load());
            p.SaveImage(args[1]);
            Monitors.Release();
            return 0;
        }
        bool autostart = args.Length == 1 && args[0] == "--autostart";
        if (args.Length > 0 && !autostart) return Cli(args);
        Layered.SetProcessDPIAware(); // scharfe Darstellung bei Windows-Skalierung

        bool first;
        using (Mutex mutex = new Mutex(true, @"Local\Helligkeit.Tray", out first))
        using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Helligkeit.Show"))
        {
            if (!first)
            {
                // läuft schon -> nur Fenster zeigen; dem laufenden Programm den Vordergrund erlauben
                Native.AllowSetForegroundWindow(-1);
                show.Set();
                return 0;
            }
            Application.EnableVisualStyles();
            Application.Run(new TrayApp(!autostart, show));
        }
        return 0;
    }

    static int Cli(string[] args)
    {
        Native.AttachConsole(-1); // Ausgabe im aufrufenden Terminal anzeigen
        Console.WriteLine();
        List<Monitor> monitors = Monitors.Load();
        try
        {
            if (args[0] == "list" || args[0] == "/?" || args[0] == "help")
            {
                for (int i = 0; i < monitors.Count; i++)
                {
                    Monitor m = monitors[i];
                    Console.WriteLine("{0}  {1,-28} {2}", i + 1, m.Name, m.Supported ? m.Percent + " %" : "kein DDC/CI");
                }
                Console.WriteLine();
                Console.WriteLine("Nutzung: Helligkeit.exe <nr|all> <0-100 | +n | -n>");
                return 0;
            }
            if (args.Length < 2) { Console.WriteLine("Nutzung: Helligkeit.exe <nr|all> <0-100 | +n | -n>"); return 1; }

            List<Monitor> targets = new List<Monitor>();
            if (args[0].ToLowerInvariant() == "all") targets.AddRange(monitors);
            else
            {
                int nr;
                if (!int.TryParse(args[0], out nr) || nr < 1 || nr > monitors.Count)
                { Console.WriteLine("Unbekannter Monitor: " + args[0] + " (siehe 'list')"); return 1; }
                targets.Add(monitors[nr - 1]);
            }

            string val = args[1];
            bool relative = val.StartsWith("+") || val.StartsWith("-");
            int n;
            if (!int.TryParse(val, out n)) { Console.WriteLine("Ungültiger Wert: " + val); return 1; }

            int rc = 0;
            foreach (Monitor m in targets)
            {
                if (!m.Supported) { Console.WriteLine(m.Name + ": kein DDC/CI"); rc = 2; continue; }
                int target = Math.Max(0, Math.Min(100, relative ? m.Percent + n : n));
                bool ok = m.Set(target);
                Console.WriteLine("{0}: {1}", m.Name, ok ? target + " %" : "Fehler beim Setzen");
                if (!ok) rc = 2;
            }
            return rc;
        }
        finally { Monitors.Release(); }
    }
}
