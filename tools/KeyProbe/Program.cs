using System.Runtime.InteropServices;
using System.Text;

// KeyProbe: shows everything that comes from keyboards and other HID devices via Raw Input.
// Press Fn, the lock key etc. - if the key sends anything, a line appears here.
// Changes nothing in the system. To exit - close the window or press Ctrl+C. The log is written to keyprobe.log next to the program.

static class KeyProbe
{
    const uint WM_INPUT = 0x00FF, RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    const uint RIDEV_INPUTSINK = 0x100, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b;
    static readonly IntPtr HWND_MESSAGE = -3;

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICELIST { public IntPtr Device; public uint Type; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint VendorId;      // HID
        [FieldOffset(12)] public uint ProductId;
        [FieldOffset(16)] public uint Version;
        [FieldOffset(20)] public ushort UsagePage;
        [FieldOffset(22)] public ushort Usage;
    }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public uint cbSize, style; public IntPtr proc; public int cls, wnd; public IntPtr inst, icon, cursor, bg; public string? menu; public string name; public IntPtr iconSm; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint n, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, char[]? data, ref uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, ref RID_DEVICE_INFO data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw, uint cmd, byte[]? data, ref uint size, uint header);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll")] static extern bool HidD_GetProductString(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetManufacturerString(IntPtr h, byte[] buf, int len);

    sealed record Dev(string Name, string Product, uint Type, uint Vid, uint Pid, ushort Page, ushort Usage);
    static readonly Dictionary<IntPtr, Dev> Devices = new();
    static readonly Dictionary<IntPtr, string> LastReport = new();
    static StreamWriter? _log;
    static WndProc? _proc;

    static void Out(string s, ConsoleColor? color = null)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {s}";
        if (color.HasValue) Console.ForegroundColor = color.Value;
        Console.WriteLine(line);
        Console.ResetColor();
        _log?.WriteLine(line);
        _log?.Flush();
    }

    static string HidString(string path, bool product)
    {
        var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // no access rights: information queries only
        if (h == -1) return "";
        try
        {
            var buf = new byte[512];
            bool ok = product ? HidD_GetProductString(h, buf, buf.Length) : HidD_GetManufacturerString(h, buf, buf.Length);
            return ok ? Encoding.Unicode.GetString(buf).TrimEnd('\0') : "";
        }
        finally { CloseHandle(h); }
    }

    static string PageName(ushort page, ushort usage) => (page, usage) switch
    {
        (0x01, 0x06) => "клавиатура",
        (0x01, 0x02) => "мышь",
        (0x01, 0x80) => "системные кнопки (питание/сон)",
        (0x0C, _) => "мультимедиа (Consumer Control)",
        (0x0D, _) => "сенсорная панель/экран",
        _ when page >= 0xFF00 || page == 0x00FF => "особые коды производителя (vendor)",
        _ => $"page 0x{page:X2}",
    };

    static void ListDevices()
    {
        uint n = 0, size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref n, size);
        var list = new RAWINPUTDEVICELIST[n];
        GetRawInputDeviceList(list, ref n, size);
        foreach (var d in list)
        {
            uint len = 0;
            GetRawInputDeviceInfo(d.Device, RIDI_DEVICENAME, null, ref len);
            var name = new char[len + 1];
            GetRawInputDeviceInfo(d.Device, RIDI_DEVICENAME, name, ref len);
            var path = new string(name).TrimEnd('\0');
            var info = new RID_DEVICE_INFO { Size = 32 };
            uint isz = 32;
            GetRawInputDeviceInfo(d.Device, RIDI_DEVICEINFO, ref info, ref isz);

            uint vid = info.Type == RIM_TYPEHID ? info.VendorId : ParseHex(path, "VID_");
            uint pid = info.Type == RIM_TYPEHID ? info.ProductId : ParseHex(path, "PID_");
            ushort page = info.Type == RIM_TYPEHID ? info.UsagePage : (ushort)1;
            ushort usage = info.Type == RIM_TYPEHID ? info.Usage : (ushort)(d.Type == RIM_TYPEKEYBOARD ? 6 : 2);
            var product = HidString(path, true);
            Devices[d.Device] = new Dev(path, product, d.Type, vid, pid, page, usage);
        }

        Out("Устройства ввода:");
        foreach (var (_, d) in Devices.OrderBy(x => x.Value.Vid).ThenBy(x => x.Value.Page))
        {
            if (d.Type == RIM_TYPEMOUSE) continue;
            bool apple = d.Vid == 0x05AC || d.Vid == 0x004C;
            Out($"  {(apple ? "APPLE " : "      ")}VID {d.Vid:X4} PID {d.Pid:X4}  page {d.Page:X4} usage {d.Usage:X4}  {PageName(d.Page, d.Usage),-36} {d.Product}",
                apple ? ConsoleColor.Yellow : null);
        }
    }

    static uint ParseHex(string s, string key)
    {
        int i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        return i >= 0 && uint.TryParse(s.AsSpan(i + key.Length, 4), System.Globalization.NumberStyles.HexNumber, null, out uint v) ? v : 0;
    }

    static void Register(IntPtr hwnd)
    {
        // Keyboards + all HID collections except mice/touchpads (they are noisy)
        var pairs = new HashSet<(ushort, ushort)> { (0x01, 0x06) };
        foreach (var d in Devices.Values)
            if (d.Type == RIM_TYPEHID && d.Page != 0x0D && !(d.Page == 0x01 && d.Usage is 0x01 or 0x02 or 0x04 or 0x05))
                pairs.Add((d.Page, d.Usage));
        foreach (var (page, usage) in pairs)
        {
            var r = new[] { new RAWINPUTDEVICE { UsagePage = page, Usage = usage, Flags = RIDEV_INPUTSINK, Target = hwnd } };
            if (!RegisterRawInputDevices(r, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                Out($"  не удалось подписаться на page {page:X4} usage {usage:X4} (ошибка {Marshal.GetLastWin32Error()})", ConsoleColor.DarkGray);
        }
    }

    static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_INPUT)
        {
            try { OnInput(l); } catch (Exception ex) { Out("Ошибка: " + ex.Message, ConsoleColor.Red); }
        }
        return DefWindowProc(h, m, w, l);
    }

    static void OnInput(IntPtr hRaw)
    {
        uint headerSize = (uint)(8 + 2 * IntPtr.Size);
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, null, ref size, headerSize);
        var buf = new byte[size];
        if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, headerSize) != size) return;
        uint type = BitConverter.ToUInt32(buf, 0);
        IntPtr dev = (IntPtr)(IntPtr.Size == 8 ? BitConverter.ToInt64(buf, 8) : BitConverter.ToInt32(buf, 8));
        int off = (int)headerSize;
        Devices.TryGetValue(dev, out var d);
        string who = d == null ? "неизвестное устройство" : $"VID {d.Vid:X4} {(d.Product.Length > 0 ? d.Product : "")}".Trim();

        if (type == RIM_TYPEKEYBOARD)
        {
            ushort make = BitConverter.ToUInt16(buf, off), flags = BitConverter.ToUInt16(buf, off + 2), vk = BitConverter.ToUInt16(buf, off + 6);
            uint msg = BitConverter.ToUInt32(buf, off + 8);
            if ((flags & 1) != 0) return; // key release - not shown
            string sc = ((flags & 2) != 0 ? "E0 " : (flags & 4) != 0 ? "E1 " : "") + make.ToString("X2");
            string vkName = vk == 0xFF ? "нет (0xFF)" : $"0x{vk:X2} {(Enum.IsDefined(typeof(ConsoleKey), (int)vk) ? ((ConsoleKey)vk).ToString() : "")}";
            Out($"КЛАВИША  скан-код {sc,-6} VK {vkName,-24} [{who}]", ConsoleColor.Green);
        }
        else if (type == RIM_TYPEHID)
        {
            int sizeHid = BitConverter.ToInt32(buf, off), count = BitConverter.ToInt32(buf, off + 4);
            for (int i = 0; i < count; i++)
            {
                var hex = BitConverter.ToString(buf, off + 8 + i * sizeHid, sizeHid).Replace('-', ' ');
                if (LastReport.TryGetValue(dev, out var last) && last == hex) continue; // repeats are not shown
                LastReport[dev] = hex;
                var where = d == null ? "" : $"page {d.Page:X4} usage {d.Usage:X4} ({PageName(d.Page, d.Usage)})";
                Out($"HID      {hex,-48} {where} [{who}]", ConsoleColor.Cyan);
            }
        }
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "KeyProbe - что присылает клавиатура";
        var logPath = Path.Combine(AppContext.BaseDirectory, "keyprobe.log");
        try { _log = new StreamWriter(logPath, false, new UTF8Encoding(true)); } catch { }

        Out("KeyProbe. Нажимайте по очереди: Fn, клавишу с замком, Fn+⌫, Fn+стрелки, затем обычную букву для сравнения.");
        Out("Зелёные строки - обычные клавиши (их можно переназначать), голубые - особые HID-сообщения.");
        Out("Если на нажатие ничего не появилось - клавиша без драйвера Apple ничего не отправляет.");
        Out($"Журнал: {logPath}");
        Out("");
        ListDevices();
        Out("");

        _proc = Proc;
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), proc = Marshal.GetFunctionPointerForDelegate(_proc), inst = GetModuleHandle(null), name = "KeyProbeWnd" };
        RegisterClassEx(ref wc);
        var hwnd = CreateWindowEx(0, wc.name, "KeyProbe", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.inst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Out("Не удалось создать окно: " + Marshal.GetLastWin32Error(), ConsoleColor.Red); return 1; }
        Register(hwnd);
        Out("Жду нажатий... (выход - закрыть окно)", ConsoleColor.White);

        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref m);
        return 0;
    }
}
