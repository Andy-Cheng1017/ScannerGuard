using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// Usage: ScannerGuard.exe [VID PID]   (default: Honeywell 1900g HID keyboard, 0C2E 0901)
string scannerVid = args.Length >= 2 ? args[0].ToUpperInvariant() : "0C2E";
string scannerPid = args.Length >= 2 ? args[1].ToUpperInvariant() : "0901";

using var instance = new Mutex(true, @"Local\ScannerGuard", out bool firstInstance);
if (!firstInstance) return;
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
try
{
    using var window = new RawInputWindow(scannerVid, scannerPid);
    Application.Run();
}
catch (Exception ex)
{
    MessageBox.Show(ex.Message, "ScannerGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

sealed record DeviceInfo(IntPtr Handle, string Name, string Vid, string Pid);

sealed class RawInputWindow : NativeWindow, IDisposable
{
    const int WM_INPUT = 0x00FF;
    const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const uint RID_INPUT = 0x10000003;
    const uint RIDI_DEVICENAME = 0x20000007;
    const uint RIM_TYPEKEYBOARD = 1;
    const uint RIDEV_INPUTSINK = 0x100;
    const uint RIDEV_DEVNOTIFY = 0x2000;
    const uint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly string _vid, _pid;
    readonly Dictionary<IntPtr, DeviceInfo> _devices = new();
    const int WH_KEYBOARD_LL = 13;
    const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10;
    static readonly UIntPtr Marker = new(0x5343414E); // tags our own injected events

    // The LL hook runs before Windows generates WM_INPUT, and a key swallowed by the hook never
    // produces WM_INPUT at all. So the device can only be learned from keys that were let through:
    // the first scanner key leaks (repaired below), after which following keys are swallowed.
    const int ActiveGapMs = 150;
    long _activeUntil;
    readonly HashSet<uint> _passedDown = new(); // keys whose down went through, so their up must too
    readonly HashSet<uint> _swallowedDown = new();
    readonly LowLevelProc _hookProc;
    readonly IntPtr _hook;
    readonly IntPtr _enUs = LoadKeyboardLayout("00000409", 0);
    bool _shift;
    bool _imeWarningShown;
    bool _inputFailed;

    public RawInputWindow(string vid, string pid)
    {
        _vid = vid; _pid = pid;
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01, usUsage = 0x06, // Generic Desktop / Keyboard
            dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, // receive even when not focused
            hwndTarget = Handle
        };
        if (!RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");

        _hookProc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        RefreshDevices();
    }

    public void Dispose()
    {
        UnhookWindowsHookEx(_hook);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) HandleInput(m.LParam);
        else if (m.Msg == WM_INPUT_DEVICE_CHANGE) RefreshDevices();
        base.WndProc(ref m);
    }

    bool IsScanner(DeviceInfo d) => d.Vid == _vid && d.Pid == _pid;

    void RefreshDevices()
    {
        _devices.Clear();
        uint count = 0, sz = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, sz) == uint.MaxValue)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, sz) == uint.MaxValue)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        foreach (var d in list.Take((int)count).Where(d => d.dwType == RIM_TYPEKEYBOARD))
        {
            var name = GetDeviceName(d.hDevice);
            var vid = Regex.Match(name, @"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
            var pid = Regex.Match(name, @"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
            var info = new DeviceInfo(d.hDevice, name, vid, pid);
            _devices[d.hDevice] = info;
        }
    }

    static string GetDeviceName(IntPtr h)
    {
        uint len = 0;
        if (GetRawInputDeviceInfo(h, RIDI_DEVICENAME, IntPtr.Zero, ref len) == uint.MaxValue)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (len == 0) throw new InvalidOperationException("Keyboard device has no name.");
        var p = Marshal.AllocHGlobal((int)len * 2);
        try
        {
            if (GetRawInputDeviceInfo(h, RIDI_DEVICENAME, p, ref len) == uint.MaxValue)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStringUni(p) ?? throw new InvalidOperationException("Invalid device name.");
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    void HandleInput(IntPtr hRawInput)
    {
        uint size = 0, hdr = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, hdr) == uint.MaxValue)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, hdr) != size)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var raw = Marshal.PtrToStructure<RAWINPUT>(buf);
            if (raw.header.dwType != RIM_TYPEKEYBOARD) return;
            var kb = raw.keyboard;
            if (kb.VKey == 0xFF) return; // fake keys generated by the system

            var dev = raw.header.hDevice;
            if (dev == IntPtr.Zero || (kb.Flags & 1) != 0) return;
            if (!_devices.TryGetValue(dev, out var info))
            {
                RefreshDevices(); // hot-plugged device
                if (!_devices.TryGetValue(dev, out info)) info = new DeviceInfo(dev, "?", "????", "????");
            }

            bool scanner = IsScanner(info);
            if (!scanner) return;
            if (Environment.TickCount64 > _activeUntil)
            {
                // This key was not swallowed (we did not know yet it was the scanner): undo it and resend.
                _activeUntil = Environment.TickCount64 + ActiveGapMs;
                bool modifier = kb.VKey is 0x10 or 0xA0 or 0xA1 or 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x14;
                if (!modifier) SendKeyPress(0x08); // Backspace removes the leaked char or IME composition symbol
                // Remove the first leaked composition symbol BEFORE closing the IME.
                // No restore is scheduled: Microsoft Bopomofo stays in English mode.
                if (!SetImeEnglish(GetForegroundWindow()) && !_imeWarningShown)
                {
                    _imeWarningShown = true;
                    BeginImeWarning();
                }
                HandleScannerKey(new KBDLLHOOKSTRUCT { vkCode = kb.VKey, scanCode = kb.MakeCode }, true);
            }
            _activeUntil = Environment.TickCount64 + ActiveGapMs;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void BeginImeWarning(string? error = null)
    {
        // Do not display a modal dialog inside the raw-input or hook callback.
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            MessageBox.Show(error ?? ("Could not confirm Microsoft Bopomofo switched to English. " +
                "The target may not support IMM control, or may be running as administrator. " +
                "Barcode interception remains active."), "ScannerGuard",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (error != null) Application.ExitThread();
        };
        timer.Start();
    }

    internal static bool SetImeEnglish(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return false;
        uint thread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var gui = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref gui)) return false;
        IntPtr ime = ImmGetDefaultIMEWnd(gui.hwndFocus != IntPtr.Zero ? gui.hwndFocus : foreground);
        if (ime == IntPtr.Zero) return false;
        // IMC_SETOPENSTATUS(FALSE) closes Bopomofo's composition mode, not its keyboard layout.
        if (SendMessageTimeout(ime, 0x0283, (IntPtr)0x0006, IntPtr.Zero,
            0x0002, 100, out _) == IntPtr.Zero) return false;
        return SendMessageTimeout(ime, 0x0283, (IntPtr)0x0005, IntPtr.Zero,
            0x0002, 100, out var open) != IntPtr.Zero && open == UIntPtr.Zero;
    }

    IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & LLKHF_INJECTED) != 0 || k.dwExtraInfo == Marker;
            bool down = (uint)wParam == WM_KEYDOWN || (uint)wParam == WM_SYSKEYDOWN;
            bool swallow = false;
            try
            {
                swallow = !_inputFailed && !injected && ShouldSwallow(k, down);
            }
            catch (Exception ex)
            {
                // Never let an exception escape the unmanaged hook callback.
                _inputFailed = true;
                UnhookWindowsHookEx(_hook);
                BeginImeWarning(ex.Message);
            }
            if (swallow) return (IntPtr)1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // While a scan is active (keys arriving in a tight burst) swallow everything and resend the
    // characters as Unicode packets, which the IME does not convert.
    bool ShouldSwallow(KBDLLHOOKSTRUCT k, bool down)
    {
        uint id = k.vkCode | ((k.flags & LLKHF_EXTENDED) << 8);
        if (!down && _passedDown.Remove(id))
        {
            if (k.vkCode is 0x10 or 0xA0 or 0xA1) _shift = false;
            return false;
        }
        bool held = !down && _swallowedDown.Remove(id);
        if (Environment.TickCount64 > _activeUntil && !held)
        {
            if (down) _passedDown.Add(id);
            return false;
        }
        _activeUntil = Environment.TickCount64 + ActiveGapMs;
        if (down) _swallowedDown.Add(id);
        HandleScannerKey(k, down);
        return true;
    }

    void HandleScannerKey(KBDLLHOOKSTRUCT k, bool down)
    {
        uint vk = k.vkCode;
        if (vk is 0x10 or 0xA0 or 0xA1) { _shift = down; return; }
        if (vk is 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x14) return;
        if (!down) return;

        if (vk is 0x0D or 0x09) { SendKeyPress((ushort)vk); return; }

        var state = new byte[256];
        if (_shift) state[0x10] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0) state[0x14] = 1;
        var sb = new System.Text.StringBuilder(4);
        if (ToUnicodeEx(vk, k.scanCode, state, sb, sb.Capacity, 4, _enUs) == 1) SendUnicode(sb[0]);
    }

    static void SendUnicode(char c)
    {
        var inputs = new INPUT[2];
        for (int i = 0; i < 2; i++)
        {
            inputs[i].type = 1;
            inputs[i].ki = new KEYBDINPUT { wScan = c, dwFlags = 0x4 | (i == 1 ? 0x2u : 0), dwExtraInfo = Marker };
        }
        SendChecked(inputs);
    }

    static void SendKeyPress(ushort vk)
    {
        var inputs = new INPUT[2];
        for (int i = 0; i < 2; i++)
        {
            inputs[i].type = 1;
            inputs[i].ki = new KEYBDINPUT { wVk = vk, dwFlags = i == 1 ? 0x2u : 0, dwExtraInfo = Marker };
        }
        SendChecked(inputs);
    }

    static void SendChecked(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "Could not send barcode input. Check target application elevation.");
    }

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message; public uint ExtraInformation; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUT { public RAWINPUTHEADER header; public RAWKEYBOARD keyboard; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] p, uint n, uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint n, uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdrSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)] static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadKeyboardLayout(string klid, uint flags);
    delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO
    {
        public uint cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder buf, int cch, uint flags, IntPtr hkl);
    #endregion
}
