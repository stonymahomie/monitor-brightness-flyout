// Brightness Flyout: control the brightness of external monitors individually via DDC/CI.
// No arguments: tray icon (left click = flyout with sliders, right click = menu).
// CLI:  BrightnessFlyout.exe list | <nr|all> <0-100|+n|-n>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Brightness Flyout")]
[assembly: AssemblyDescription("Control the brightness of external monitors via DDC/CI")]
[assembly: AssemblyProduct("Brightness Flyout")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

// UI language follows Windows: German on German systems, English everywhere else.
static class L
{
    static readonly bool German =
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

    public static string T(string english, string german) { return German ? german : english; }
}

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
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

class Monitor
{
    public string Id;   // stable hardware id, e.g. DISPLAY\VSCD73A\5&1B33B6&0&UID24834
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
            for (int i = 0; i < 3; i++) // DDC/CI sometimes only answers on the second try
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

            string key = DeviceKey(info.szDevice);
            string name = LookupName(key, names);
            for (int i = 0; i < count; i++)
            {
                Monitor m = new Monitor();
                m.Handle = phys[i].hPhysicalMonitor;
                m.Left = info.rcMonitor.Left;
                m.Name = name ?? phys[i].szPhysicalMonitorDescription;
                m.Id = (key ?? m.Name) + (count > 1 ? "#" + i : "");
                result.Add(m);
            }
            return true;
        }, IntPtr.Zero);

        result.Sort(delegate (Monitor a, Monitor b) { return a.Left.CompareTo(b.Left); }); // left to right
        foreach (Monitor m in result) m.Read();
        return result;
    }

    public static void Release()
    {
        foreach (Native.PHYSICAL_MONITOR[] p in allocated) Native.DestroyPhysicalMonitors((uint)p.Length, p);
        allocated.Clear();
    }

    // Hardware id of the monitor on adapter \\.\DISPLAYn, e.g. DISPLAY\VSCD73A\5&1B33B6&0&UID24834.
    static string DeviceKey(string adapter)
    {
        Native.DISPLAY_DEVICE dd = new Native.DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
        // Flag 1 = EDD_GET_DEVICE_INTERFACE_NAME -> \\?\DISPLAY#VSCD73A#5&...&UID24834#{guid}
        if (!Native.EnumDisplayDevices(adapter, 0, ref dd, 1)) return null;
        string[] parts = dd.DeviceID.Split('#');
        if (parts.Length < 3) return null;
        return ("DISPLAY\\" + parts[1] + "\\" + parts[2]).ToUpperInvariant();
    }

    // Model name from the EDID data (WmiMonitorID) for a hardware id.
    static string LookupName(string key, Dictionary<string, string> names)
    {
        if (key == null) return null;
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

// Remembers which monitors were connected when the DDC/CI hint was dismissed.
// The hint comes back as soon as a monitor shows up that wasn't in that list.
static class HintSettings
{
    const string Key = @"Software\BrightnessFlyout";
    const string Value = "HintDismissedFor";

    public static bool Dismissed(List<Monitor> monitors)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key))
            {
                string[] known = k == null ? null : k.GetValue(Value) as string[];
                if (known == null) return false;
                foreach (Monitor m in monitors)
                    if (Array.IndexOf(known, m.Id) < 0) return false; // new monitor -> show again
                return true;
            }
        }
        catch { return false; }
    }

    public static void Dismiss(List<Monitor> monitors)
    {
        try
        {
            string[] ids = new string[monitors.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = monitors[i].Id;
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                k.SetValue(Value, ids, Microsoft.Win32.RegistryValueKind.MultiString);
        }
        catch { }
    }
}

static class Theme
{
    // Personalize: AppsUseLightTheme (windows), SystemUsesLightTheme (taskbar)
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

    // Accent color in the shade Windows itself uses for sliders
    // (AccentPalette: 0-2 light, 3 base, 4-6 dark; dark theme -> light shade, light theme -> dark shade).
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

    // Sun icon for the notification area, matching the taskbar color.
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

// Win32 APIs for the translucent, custom-drawn flyout.
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

    // Show a finished image (with alpha channel) at pos with overall opacity alpha.
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

    // Blur behind the window (like the Windows flyouts).
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

// Flyout styled like the Windows 11 quick settings:
// rounded, translucent, slides in from below. Everything is drawn by hand.
class BrightnessPanel : Form
{
    const int RowH = 58, Header = 46, SepH = 9, Pad = 16, Radius = 8, Slide = 40, AnimMs = 260, FooterH = 50;

    readonly List<Monitor> monitors;
    readonly int[] values;   // [0] = all monitors, [i+1] = monitor i
    readonly bool[] enabled;
    readonly System.Windows.Forms.Timer[] debounce;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    readonly bool light;
    readonly Color accent;
    readonly float s; // DPI scale
    readonly int W;
    int H;
    bool showHint;  // a monitor without DDC/CI and the hint wasn't dismissed for this set of monitors
    bool hoverClose;
    // Typing a value: click the percentage to turn it into a small text box
    readonly System.Windows.Forms.Timer caret = new System.Windows.Forms.Timer();
    int editRow = -1, hoverValue = -1;
    string editText = "";
    bool editAll, caretOn; // editAll = whole number selected, the next digit replaces it
    readonly Font fTitle, fLabel, fValue, fIcon;
    Point target, pos;
    int visibleBottom = int.MaxValue; // bottom of the work area: anything below is clipped
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
            t.Interval = 120; // DDC/CI is slow: only send once the slider rests briefly
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

        showHint = monitors.Exists(delegate (Monitor m) { return !m.Supported; })
                   && !HintSettings.Dismissed(monitors);
        W = S(380);
        H = PanelHeight();
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
        caret.Interval = 530; // Windows caret blink rate
        caret.Tick += delegate { caretOn = !caretOn; Redraw(); };
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

    int PanelHeight() { return S(Header + values.Length * RowH + SepH + 8 + (showHint ? FooterH : 0)); }

    static string FontOr(string wanted, string fallback)
    {
        foreach (FontFamily f in FontFamily.Families) if (f.Name == wanted) return wanted;
        return fallback;
    }

    // Rounded window shape; during the animation, clip everything below the taskbar edge.
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
        Layered.SetWindowRgn(Handle, round, true); // Windows takes ownership of the region
    }

    // ---------- Data ----------

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

    // ---------- Show / animation ----------

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
        // Force to the very top; TopMost alone is not reliable here.
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
        double eased = 1 - Math.Pow(1 - t, 3); // fast start, gentle stop
        pos = new Point(target.X, target.Y + (int)Math.Round(S(Slide) * (1 - eased)));
        SetClip();
        Render((byte)(255 * Math.Min(1.0, t * 1.8)));
        if (t >= 1) anim.Stop();
    }

    void ClosePanel()
    {
        if (!Visible) return;
        CommitEdit(); // a typed value is applied, like a text box losing focus
        anim.Stop();
        dragRow = -1; hoverRow = -1;
        Hide();
        LastHidden = DateTime.Now;
    }

    void Redraw() { if (IsHandleCreated && Visible && !anim.Enabled) Render(255); }

    // ---------- Drawing ----------

    int RowTop(int i) { return S(Header + i * RowH + (i >= 1 ? SepH : 0)); }
    int TrackX0 { get { return S(Pad + 34); } }
    int TrackX1 { get { return W - S(Pad + 64); } }
    int TrackY(int i) { return RowTop(i) + S(38); }

    // Clickable percentage / text box area right of the slider.
    Rectangle ValueRect(int i) { return new Rectangle(TrackX1 + S(12), TrackY(i) - S(14), W - S(Pad) - TrackX1 - S(12), S(28)); }

    void Render(byte alpha)
    {
        using (Bitmap b = Draw()) Layered.Push(Handle, b, pos, alpha);
    }

    // For checking the look without a screen: BrightnessFlyout.exe --render file.png
    public void SaveImage(string file) { using (Bitmap b = Draw()) b.Save(file); }

    Bitmap Draw()
    {
        Bitmap b = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
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
                g.DrawString(L.T("Brightness", "Helligkeit"), fTitle, br, S(Pad) - S(1), S(14));

            int sepY = S(Header + RowH + SepH / 2.0);
            using (Pen pen = new Pen(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255), 1f))
                g.DrawLine(pen, S(Pad), sepY, W - S(Pad), sepY);

            for (int i = 0; i < values.Length; i++) DrawRow(g, i, fg);
            if (showHint) DrawHint(g, fg);
        }
        return b;
    }

    int HintTop { get { return RowTop(values.Length) + S(2); } }

    // Close button of the hint, top right of the footer.
    Rectangle CloseRect { get { return new Rectangle(W - S(Pad) - S(24), HintTop + S(8), S(24), S(24)); } }

    // Hide the hint for good (until a new monitor shows up). The flyout shrinks, its bottom edge stays put.
    void DismissHint()
    {
        HintSettings.Dismiss(monitors);
        showHint = false;
        hoverClose = false;
        int oldH = H;
        H = PanelHeight();
        pos = new Point(pos.X, pos.Y + (oldH - H));
        target = new Point(target.X, target.Y + (oldH - H));
        Size = new Size(W, H);
        SetClip();
        Render(255);
    }

    // Footer with a short how-to for monitors whose slider is locked.
    void DrawHint(Graphics g, Color fg)
    {
        int top = HintTop;
        Color fg2 = Color.FromArgb(light ? 165 : 200, fg);
        using (Pen pen = new Pen(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255), 1f))
            g.DrawLine(pen, S(Pad), top, W - S(Pad), top);

        StringFormat center = new StringFormat();
        center.Alignment = StringAlignment.Center;
        center.LineAlignment = StringAlignment.Center;
        using (SolidBrush br = new SolidBrush(fg2))
        {
            // E946 = info icon
            g.DrawString("", fIcon, br, new RectangleF(S(Pad) - S(2), top + S(8), S(26), S(26)), center);
            g.DrawString(L.T("Locked slider: turn on DDC/CI in the monitor's own menu. Not every monitor supports it.",
                             "Gesperrter Regler: DDC/CI im Menü des Monitors einschalten. Nicht jeder Monitor kann das."),
                fLabel, br, new RectangleF(TrackX0, top + S(6), W - TrackX0 - S(Pad) - S(30), S(FooterH - 8)));
        }

        // × button (E711 = cancel icon) with a subtle hover background like Windows
        Rectangle cr = CloseRect;
        if (hoverClose)
            using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(cr.X, cr.Y, cr.Width, cr.Height, S(4)))
            using (SolidBrush br = new SolidBrush(light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255)))
                g.FillPath(br, p);
        using (Font f = new Font(fIcon.FontFamily, 8f * s, GraphicsUnit.Point))
        using (SolidBrush br = new SolidBrush(hoverClose ? fg : fg2))
            g.DrawString("", f, br, cr, center);
    }

    protected override void Dispose(bool disposing)
    {
        // Stop pending timers so nothing is sent to monitor handles that are about to be released.
        if (disposing)
        {
            anim.Stop();
            caret.Stop();
            foreach (System.Windows.Forms.Timer t in debounce) t.Stop();
        }
        base.Dispose(disposing);
    }

    void DrawRow(Graphics g, int i, Color fg)
    {
        bool en = enabled[i];
        Color fg2 = Color.FromArgb(en ? (light ? 165 : 200) : 110, fg);
        Color rail = light ? Color.FromArgb(en ? 115 : 50, 0, 0, 0) : Color.FromArgb(en ? 140 : 55, 255, 255, 255);
        int top = RowTop(i);
        float cy = TrackY(i);
        int x0 = TrackX0, x1 = TrackX1;

        string label = i == 0 ? L.T("All monitors", "Alle Monitore") : i + "   " + monitors[i - 1].Name;
        using (SolidBrush br = new SolidBrush(fg2)) g.DrawString(label, fLabel, br, S(Pad) - S(1), top + S(6));

        // Icons from Segoe Fluent Icons: E706 = brightness, E7F4 = monitor
        StringFormat center = new StringFormat();
        center.Alignment = StringAlignment.Center;
        center.LineAlignment = StringAlignment.Center;
        using (SolidBrush br = new SolidBrush(en ? fg : fg2))
            g.DrawString(i == 0 ? "\uE706" : "\uE7F4", fIcon, br, new RectangleF(S(Pad) - S(2), cy - S(13), S(26), S(26)), center);

        // Rail + filled part in the accent color
        float th = S(4);
        float xv = x0 + (x1 - x0) * values[i] / 100f;
        using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(x0, cy - th / 2, x1 - x0, th, th / 2))
        using (SolidBrush br = new SolidBrush(rail)) g.FillPath(br, p);
        if (en && xv > x0 + 1)
            using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(x0, cy - th / 2, xv - x0, th, th / 2))
            using (SolidBrush br = new SolidBrush(accent)) g.FillPath(br, p);

        // Thumb: outer ring; inner dot grows on hover and shrinks while dragging
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
        if (en && editRow == i) { DrawEditBox(g, i, fg, fg2); return; }

        Rectangle vr = ValueRect(i);
        if (en && hoverValue == i) // hint that the number can be clicked
            using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(vr.X, vr.Y, vr.Width, vr.Height, S(4)))
            using (SolidBrush br = new SolidBrush(light ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255)))
                g.FillPath(br, p);

        using (SolidBrush br = new SolidBrush(en ? fg : fg2))
        {
            if (en)
                g.DrawString(values[i] + " %", fValue, br,
                    new RectangleF(vr.X, vr.Y, vr.Width - S(7), vr.Height), right);
            else // note goes right-aligned on the name line so it doesn't wrap
                g.DrawString(L.T("no DDC/CI", "kein DDC/CI"), fLabel, br,
                    new RectangleF(S(Pad), top + S(4), W - S(Pad * 2), S(22)), right);
        }
    }

    // Windows 11 style text box: filled background, thin border, accent line at the bottom.
    void DrawEditBox(Graphics g, int i, Color fg, Color fg2)
    {
        Rectangle vr = ValueRect(i);
        using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(vr.X + 0.5f, vr.Y + 0.5f, vr.Width - 1, vr.Height - 1, S(4)))
        {
            using (SolidBrush br = new SolidBrush(light ? Color.White : Color.FromArgb(31, 31, 31))) g.FillPath(br, p);
            using (Pen pen = new Pen(light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(35, 255, 255, 255), 1f)) g.DrawPath(pen, p);
        }
        using (SolidBrush br = new SolidBrush(accent))
            g.FillRectangle(br, vr.X + S(1), vr.Bottom - S(2), vr.Width - S(2), S(2));

        StringFormat tf = (StringFormat)StringFormat.GenericTypographic.Clone();
        tf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        float th = fValue.GetHeight(g);
        float ty = TrackY(i) - th / 2;
        string pct = " %";
        float pw = g.MeasureString(pct, fValue, PointF.Empty, tf).Width;
        float dw = editText.Length == 0 ? 0 : g.MeasureString(editText, fValue, PointF.Empty, tf).Width;
        float right = vr.Right - S(7) - pw;
        float dx = right - dw;

        using (SolidBrush br = new SolidBrush(fg2)) g.DrawString(pct, fValue, br, right, ty, tf);
        if (editAll && dw > 0) // selected number: accent background like a text selection
        {
            using (SolidBrush br = new SolidBrush(accent)) g.FillRectangle(br, dx - S(1), ty, dw + S(2), th);
            using (SolidBrush br = new SolidBrush(light ? Color.White : Color.Black)) g.DrawString(editText, fValue, br, dx, ty, tf);
        }
        else
        {
            using (SolidBrush br = new SolidBrush(fg)) g.DrawString(editText, fValue, br, dx, ty, tf);
            if (caretOn)
                using (Pen pen = new Pen(fg, Math.Max(1f, s)))
                    g.DrawLine(pen, right + S(1), ty + S(2), right + S(1), ty + th - S(2));
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

    // ---------- Mouse / keyboard ----------

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
        int v = HitValue(e.Location);
        if (editRow >= 0)
        {
            if (v == editRow) { editAll = true; Redraw(); return; } // click into the box again: select all
            CommitEdit();
        }
        if (v >= 0) { BeginEdit(v); return; }
        if (showHint && CloseRect.Contains(e.Location)) { DismissHint(); return; }
        int r = HitRow(e.Location, false);
        if (r >= 0 && enabled[r]) { dragRow = r; Capture = true; SetFromX(r, e.X); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragRow >= 0) { SetFromX(dragRow, e.X); return; }
        int hv = HitValue(e.Location);
        int r = hv >= 0 ? -1 : HitRow(e.Location, false);
        bool hc = showHint && CloseRect.Contains(e.Location);
        Cursor = hv >= 0 ? Cursors.IBeam : Cursors.Default;
        if (r != hoverRow || hc != hoverClose || hv != hoverValue)
        {
            hoverRow = r; hoverClose = hc; hoverValue = hv;
            Redraw();
        }
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
        if ((hoverRow >= 0 || hoverClose || hoverValue >= 0) && dragRow < 0)
        {
            hoverRow = -1; hoverClose = false; hoverValue = -1;
            Redraw();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        CommitEdit();
        int r = HitRow(e.Location, true);
        if (r >= 0 && enabled[r]) SetRow(r, values[r] + (e.Delta > 0 ? 2 : -2));
    }

    // ---------- Typing a value ----------

    int HitValue(Point p)
    {
        for (int i = 0; i < values.Length; i++)
            if (enabled[i] && ValueRect(i).Contains(p)) return i;
        return -1;
    }

    void BeginEdit(int row)
    {
        editRow = row;
        editText = values[row].ToString();
        editAll = true;
        caretOn = true;
        caret.Stop(); caret.Start();
        Redraw();
    }

    void EndEdit()
    {
        editRow = -1;
        caret.Stop();
    }

    // Apply the typed number (clamped to 0-100). Empty input changes nothing.
    void CommitEdit()
    {
        if (editRow < 0) return;
        int row = editRow, v;
        string text = editText;
        EndEdit();
        if (int.TryParse(text, out v)) SetRow(row, v);
        else Redraw();
    }

    void EditKeepsCaretVisible() { caretOn = true; caret.Stop(); caret.Start(); Redraw(); }

    // Arrow keys would otherwise be used for focus navigation and never reach OnKeyDown.
    protected override bool IsInputKey(Keys keyData)
    {
        if (editRow >= 0 && (keyData == Keys.Up || keyData == Keys.Down)) return true;
        return base.IsInputKey(keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (editRow >= 0 && (keyData == Keys.Up || keyData == Keys.Down))
        {
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (editRow < 0 || !char.IsDigit(e.KeyChar)) return;
        e.Handled = true;
        if (editAll) { editText = ""; editAll = false; }
        if (editText.Length < 3) editText += e.KeyChar;
        EditKeepsCaretVisible();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (editRow >= 0)
        {
            int cur;
            switch (e.KeyCode)
            {
                case Keys.Enter: CommitEdit(); break;
                case Keys.Escape: EndEdit(); Redraw(); break; // cancel typing, keep the flyout open
                case Keys.Back:
                    editText = editAll || editText.Length == 0 ? "" : editText.Substring(0, editText.Length - 1);
                    editAll = false;
                    EditKeepsCaretVisible();
                    break;
                case Keys.Delete: editText = ""; editAll = false; EditKeepsCaretVisible(); break;
                case Keys.Up:
                case Keys.Down:
                    if (!int.TryParse(editText, out cur)) cur = values[editRow];
                    cur = Math.Max(0, Math.Min(100, cur + (e.KeyCode == Keys.Up ? 1 : -1)));
                    editText = cur.ToString();
                    editAll = false;
                    EditKeepsCaretVisible();
                    break;
                default: return; // digits arrive in OnKeyPress
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape) ClosePanel();
    }
}

// Lives in the notification area: left click = flyout, right click = menu.
class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "BrightnessFlyout";
    const string OldRunValue = "Helligkeit"; // name used before v1.1.0
    readonly NotifyIcon icon = new NotifyIcon();
    readonly Control invoker = new Control(); // for calls from other threads
    readonly ToolStripMenuItem autostart;
    readonly System.Windows.Forms.Timer redetect = new System.Windows.Forms.Timer();
    List<Monitor> monitors;
    BrightnessPanel panel;

    public TrayApp(bool showPanel, EventWaitHandle showSignal)
    {
        invoker.CreateControl();
        MigrateAutostart();
        monitors = Monitors.Load();
        BuildPanel();

        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripItem open = menu.Items.Add(L.T("Open brightness", "Helligkeit öffnen"), null, delegate { panel.ShowNearTray(); });
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        foreach (int p in new int[] { 100, 75, 50, 25 })
        {
            int preset = p;
            menu.Items.Add(L.T("All to ", "Alle auf ") + p + " %", null, delegate { SetAll(preset); });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Re-detect monitors", "Monitore neu erkennen"), null, delegate { Reload(true); });
        autostart = new ToolStripMenuItem(L.T("Start with Windows", "Mit Windows starten"), null, delegate { ToggleAutostart(); });
        autostart.Checked = AutostartEnabled();
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Exit", "Beenden"), null, delegate { Quit(); });

        icon.Icon = Theme.SunIcon();
        icon.ContextMenuStrip = menu;
        icon.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) TogglePanel(); };
        icon.Visible = true;
        UpdateTooltip();

        // A second launch shows the flyout of the running instance instead of starting again.
        Thread waiter = new Thread(delegate ()
        {
            while (showSignal.WaitOne())
                invoker.BeginInvoke((MethodInvoker)delegate { panel.ShowNearTray(); });
        });
        waiter.IsBackground = true;
        waiter.Start();

        // Monitor plugged in/out or switched on/off -> Windows reports a display change.
        // Wait a moment until the monitor answers on DDC/CI, then reload the list.
        redetect.Interval = 2000;
        redetect.Tick += delegate { redetect.Stop(); Reload(panel.Visible); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        if (showPanel) panel.ShowNearTray();
    }

    void OnDisplayChanged(object sender, EventArgs e)
    {
        // May arrive on another thread: restart the delay on the UI thread.
        invoker.BeginInvoke((MethodInvoker)delegate { redetect.Stop(); redetect.Start(); });
    }

    void BuildPanel()
    {
        panel = new BrightnessPanel(monitors);
        panel.ValuesChanged += delegate { UpdateTooltip(); };
        IntPtr h = panel.Handle; // create the window handle now so BeginInvoke works
    }

    void TogglePanel()
    {
        if (panel.Visible) { panel.Hide(); return; }
        // Clicking the icon takes focus from the open flyout -> it has only just closed itself.
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

    void Reload(bool showAfter)
    {
        panel.Dispose();
        Monitors.Release();
        monitors = Monitors.Load();
        BuildPanel();
        UpdateTooltip();
        if (showAfter) panel.ShowNearTray();
    }

    void UpdateTooltip()
    {
        string text = L.T("Brightness", "Helligkeit");
        for (int i = 0; i < monitors.Count; i++)
            if (monitors[i].Supported) text += "\n" + (i + 1) + ": " + monitors[i].Percent + " %";
        icon.Text = text.Length > 63 ? text.Substring(0, 63) : text; // Windows limit
    }

    static bool AutostartEnabled()
    {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunValue) != null;
    }

    static string AutostartCommand() { return "\"" + Application.ExecutablePath + "\" --autostart"; }

    // Before v1.1.0 the autostart entry was called "Helligkeit": move it to the new name and path.
    static void MigrateAutostart()
    {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (k.GetValue(OldRunValue) == null) return;
            k.DeleteValue(OldRunValue, false);
            k.SetValue(RunValue, AutostartCommand());
        }
    }

    void ToggleAutostart()
    {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (AutostartEnabled()) k.DeleteValue(RunValue, false);
            else k.SetValue(RunValue, AutostartCommand());
        }
        autostart.Checked = AutostartEnabled();
    }

    void Quit()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        redetect.Stop();
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
        if (args.Length >= 2 && args[0] == "--render") // --render file.png [culture, e.g. en-US]
        {
            if (args.Length >= 3) Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(args[2]);
            Layered.SetProcessDPIAware();
            BrightnessPanel p = new BrightnessPanel(Monitors.Load());
            p.SaveImage(args[1]);
            Monitors.Release();
            return 0;
        }
        bool autostart = args.Length == 1 && args[0] == "--autostart";
        if (args.Length > 0 && !autostart) return Cli(args);
        Layered.SetProcessDPIAware(); // sharp rendering with Windows display scaling

        bool first;
        using (Mutex mutex = new Mutex(true, @"Local\BrightnessFlyout.Tray", out first))
        using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BrightnessFlyout.Show"))
        {
            if (!first)
            {
                // Already running -> just show the flyout; allow the running instance to take the foreground.
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
        Native.AttachConsole(-1); // print to the calling terminal
        Console.WriteLine();
        string usage = L.T("Usage", "Nutzung") + ": BrightnessFlyout.exe <nr|all> <0-100 | +n | -n>";
        List<Monitor> monitors = Monitors.Load();
        try
        {
            if (args[0] == "list" || args[0] == "/?" || args[0] == "help")
            {
                for (int i = 0; i < monitors.Count; i++)
                {
                    Monitor m = monitors[i];
                    Console.WriteLine("{0}  {1,-28} {2}", i + 1, m.Name, m.Supported ? m.Percent + " %" : L.T("no DDC/CI", "kein DDC/CI"));
                }
                Console.WriteLine();
                if (monitors.Exists(delegate (Monitor m) { return !m.Supported; }))
                {
                    Console.WriteLine(L.T("No DDC/CI: turn it on in the monitor's own menu. Not every monitor supports it.",
                                          "Kein DDC/CI: im Menü des Monitors einschalten. Nicht jeder Monitor kann das."));
                    Console.WriteLine();
                }
                Console.WriteLine(usage);
                return 0;
            }
            if (args.Length < 2) { Console.WriteLine(usage); return 1; }

            List<Monitor> targets = new List<Monitor>();
            if (args[0].ToLowerInvariant() == "all") targets.AddRange(monitors);
            else
            {
                int nr;
                if (!int.TryParse(args[0], out nr) || nr < 1 || nr > monitors.Count)
                {
                    Console.WriteLine(L.T("Unknown monitor: ", "Unbekannter Monitor: ") + args[0] + L.T(" (see 'list')", " (siehe 'list')"));
                    return 1;
                }
                targets.Add(monitors[nr - 1]);
            }

            string val = args[1];
            bool relative = val.StartsWith("+") || val.StartsWith("-");
            int n;
            if (!int.TryParse(val, out n)) { Console.WriteLine(L.T("Invalid value: ", "Ungültiger Wert: ") + val); return 1; }

            int rc = 0;
            foreach (Monitor m in targets)
            {
                if (!m.Supported) { Console.WriteLine(m.Name + ": " + L.T("no DDC/CI", "kein DDC/CI")); rc = 2; continue; }
                int target = Math.Max(0, Math.Min(100, relative ? m.Percent + n : n));
                bool ok = m.Set(target);
                Console.WriteLine("{0}: {1}", m.Name, ok ? target + " %" : L.T("failed to set", "Fehler beim Setzen"));
                if (!ok) rc = 2;
            }
            return rc;
        }
        finally { Monitors.Release(); }
    }
}
