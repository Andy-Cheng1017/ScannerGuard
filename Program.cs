using System.Runtime.InteropServices;
using System.Windows.Forms;

using var instance = new Mutex(true, @"Local\ScannerGuard", out bool firstInstance);
if (!firstInstance) return;
Diagnostics.Initialize();
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
try
{
    using var context = new ScannerGuardApplicationContext();
    Application.Run(context);
    Diagnostics.Complete();
}
catch (Exception ex)
{
    Diagnostics.Fatal("main", ex);
    Environment.ExitCode = 1;
    MessageBox.Show($"{ex.Message}\n\nDiagnostics: {Diagnostics.LogDirectory}",
        "ScannerGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

sealed class ScannerGuardApplicationContext : ApplicationContext
{
    readonly RawInputWindow _window;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _status;
    readonly System.Windows.Forms.Timer _heartbeat = new() { Interval = 60_000 };
    bool _keepAwake;
    bool _stopped;

    public ScannerGuardApplicationContext()
    {
        _status = new ToolStripMenuItem { Enabled = false };
        _window = new RawInputWindow(UpdateKeyboardStatus);
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripMenuItem("將下一個鍵盤設為一般鍵盤", null,
            (_, _) => _window.SelectPrimaryOnNextKey()));
        menu.Items.Add(new ToolStripMenuItem("重新自動偵測內建鍵盤", null,
            (_, _) => _window.UseAutomaticPrimary()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("開啟診斷資料夾", null, (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Diagnostics.LogDirectory) { UseShellExecute = true })));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) =>
        {
            Diagnostics.Write("EXIT_REQUEST", "source=tray");
            ExitThread();
        }));

        if (SetKeepAwake(true))
            _keepAwake = true;
        else
            MessageBox.Show("Could not prevent automatic sleep or display timeout.",
                "ScannerGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ScannerGuard - barcode mode helper",
            ContextMenuStrip = menu,
            Visible = true
        };
        UpdateKeyboardStatus(_window.KeyboardStatus);
        _heartbeat.Tick += (_, _) => Diagnostics.Write("HEARTBEAT",
            $"uiResponsive=true keepAwakeRequested={_keepAwake}");
        _heartbeat.Start();
        Diagnostics.Write("READY", $"keepAwakeRequested={_keepAwake}");
    }

    void UpdateKeyboardStatus(string status)
    {
        _status.Text = status.Length <= 80 ? status : status[..77] + "...";
    }

    protected override void ExitThreadCore()
    {
        Shutdown();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Shutdown();
        base.Dispose(disposing);
    }

    void Shutdown()
    {
        if (_stopped) return;
        _stopped = true;
        Diagnostics.Write("STOPPING", "disposing tray and input handler");
        _heartbeat.Dispose();
        _tray.Visible = false;
        var menu = _tray.ContextMenuStrip;
        _tray.Dispose();
        menu?.Dispose();
        _window.Dispose();
        if (_keepAwake) SetKeepAwake(false);
    }

    internal static bool SetKeepAwake(bool enabled)
    {
        bool success = SetThreadExecutionState(enabled ? 0x80000003u : 0x80000000u) != UIntPtr.Zero;
        int error = success ? 0 : Marshal.GetLastWin32Error();
        Diagnostics.Write("POWER", $"enabled={enabled} success={success} win32Error={error}");
        return success;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern UIntPtr SetThreadExecutionState(uint flags);
}

sealed record DeviceInfo(IntPtr Handle, string Name);

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

    readonly Dictionary<IntPtr, DeviceInfo> _devices = new();
    readonly HashSet<string> _primaryPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<string> _statusChanged;
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
    sealed record PassedKey(KBDLLHOOKSTRUCT Key, byte Shift, bool CapsLock, long Tick);
    readonly List<PassedKey> _passedKeys = new();
    readonly System.Windows.Forms.Timer _englishTimer = new();
    readonly Action<char> _sendCharacter = SendUnicode;
    readonly Action<ushort> _sendKeyPress = SendKeyPress;
    readonly Action<ushort> _releaseKey = SendKeyRelease;
    readonly LowLevelProc _hookProc;
    readonly IntPtr _hook;
    readonly IntPtr _enUs = LoadKeyboardLayout("00000409", 0);
    byte _shiftMask;
    IntPtr _scanForeground, _scanFocus;
    bool _imeWarningShown;
    bool _inputFailed;
    bool _selectingPrimary;
    string? _manualPrimaryPath;

    public RawInputWindow(Action<string> statusChanged)
    {
        _statusChanged = statusChanged;
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
        _englishTimer.Tick += OnEnglishTimerTick;
        RefreshDevices();
    }

    public void Dispose()
    {
        _englishTimer.Dispose();
        UnhookWindowsHookEx(_hook);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) HandleInput(m.LParam);
        else if (m.Msg == WM_INPUT_DEVICE_CHANGE) RefreshDevices();
        base.WndProc(ref m);
    }

    internal string KeyboardStatus => _selectingPrimary
        ? "等待指定一般鍵盤：請按它的任一鍵"
        : _primaryPaths.Count > 0
            ? $"一般鍵盤已辨識 ({_primaryPaths.Count} 個)；其他鍵盤作為掃描器"
            : "尚未辨識一般鍵盤；請用選單指定";

    internal void UpdateKeyboardStatus() => _statusChanged(KeyboardStatus);

    internal void SelectPrimaryOnNextKey()
    {
        _selectingPrimary = true;
        UpdateKeyboardStatus();
    }

    internal void UseAutomaticPrimary()
    {
        _manualPrimaryPath = null;
        _selectingPrimary = false;
        RefreshDevices();
    }

    internal static bool IsBuiltInKeyboardPath(string name) =>
        name.StartsWith(@"\\?\ACPI#", StringComparison.OrdinalIgnoreCase);

    internal static bool IsScannerPath(string name, ISet<string> primaryPaths) =>
        primaryPaths.Count > 0 && !primaryPaths.Contains(name);

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
            _devices[d.hDevice] = new DeviceInfo(d.hDevice, name);
        }
        _primaryPaths.Clear();
        if (_manualPrimaryPath != null)
        {
            if (_devices.Values.Any(device => string.Equals(device.Name, _manualPrimaryPath, StringComparison.OrdinalIgnoreCase)))
                _primaryPaths.Add(_manualPrimaryPath);
            else
                _manualPrimaryPath = null;
        }
        if (_manualPrimaryPath == null)
            foreach (var device in _devices.Values.Where(device => IsBuiltInKeyboardPath(device.Name)))
                _primaryPaths.Add(device.Name);
        _statusChanged(KeyboardStatus);
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
                if (!_devices.TryGetValue(dev, out info)) return;
            }

            if (_selectingPrimary)
            {
                _manualPrimaryPath = info.Name;
                _selectingPrimary = false;
                RefreshDevices();
            }
            bool scanner = IsScannerPath(info.Name, _primaryPaths);
            HandlePassedScannerKey(kb.VKey, kb.MakeCode, (kb.Flags & 2) != 0, scanner);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void HandlePassedScannerKey(ushort vk, ushort scan, bool extended, bool scanner)
    {
        int index = _passedKeys.FindIndex(p => p.Key.scanCode == scan &&
            ((p.Key.flags & LLKHF_EXTENDED) != 0) == extended);
        if (index < 0)
        {
            if (!scanner) return;
            Diagnostics.Write("INPUT_MATCH_FAILED",
                $"pendingCount={_passedKeys.Count} activeScan={Environment.TickCount64 <= _activeUntil}");
            throw new InvalidOperationException("Scanner input could not be matched to a keyboard event; stopping to avoid duplicate input.");
        }
        var passed = _passedKeys[index];
        _passedKeys.RemoveAt(index);
        if (!scanner) return;
        bool starting = Environment.TickCount64 > _activeUntil;
        if (starting)
        {
            _scanForeground = GetForegroundWindow();
            _scanFocus = GetFocusedWindow(_scanForeground);
        }
        _activeUntil = Environment.TickCount64 + ActiveGapMs;

        if (IsShift(vk))
        {
            // Release the already-delivered Shift before emitting Unicode. Its later physical
            // release must be swallowed, while our own Shift state remains independent.
            uint id = KeyId(passed.Key);
            if (_passedDown.Remove(id))
            {
                _swallowedDown.Add(id);
                _releaseKey((ushort)ShiftVirtualKey(passed.Key));
            }
        }
        else if (vk is not (0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x14 or 0x0D or 0x09))
        {
            _sendKeyPress(0x08);
            // Use the state at key-down, not the state after a later Shift-up callback.
            EmitScannerKey(passed.Key, passed.Shift, passed.CapsLock);
        }
        if (starting) EnsureEnglish();
        ScheduleEnglish();
    }

    static bool IsShift(uint vk) => vk is 0x10 or 0xA0 or 0xA1;

    static uint ShiftVirtualKey(KBDLLHOOKSTRUCT key) =>
        key.vkCode == 0xA1 || key.scanCode == 0x36 ? 0xA1u : 0xA0u;

    static uint KeyId(KBDLLHOOKSTRUCT key) =>
        (IsShift(key.vkCode) ? ShiftVirtualKey(key) : key.vkCode) |
        ((key.flags & LLKHF_EXTENDED) << 8);

    void TrackShift(KBDLLHOOKSTRUCT key, bool down)
    {
        if (!IsShift(key.vkCode)) return;
        byte bit = ShiftVirtualKey(key) == 0xA1 ? (byte)2 : (byte)1;
        _shiftMask = down ? (byte)(_shiftMask | bit) : (byte)(_shiftMask & ~bit);
    }

    void ScheduleEnglish()
    {
        _englishTimer.Stop();
        _englishTimer.Interval = ActiveGapMs + 75;
        _englishTimer.Start();
        _activeUntil = Environment.TickCount64 + ActiveGapMs;
    }

    void OnEnglishTimerTick(object? sender, EventArgs e)
    {
        _englishTimer.Stop();
        if (!_inputFailed) EnsureEnglish();
    }

    static IntPtr GetFocusedWindow(IntPtr foreground)
    {
        uint thread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        if (foreground == IntPtr.Zero || thread == 0) return IntPtr.Zero;
        var gui = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(thread, ref gui) ? gui.hwndFocus : IntPtr.Zero;
    }

    void EnsureEnglish()
    {
        // A finite scan-completion retry, never a permanent "force English" loop.
        bool sameTarget = _scanForeground != IntPtr.Zero && _scanFocus != IntPtr.Zero &&
            GetForegroundWindow() == _scanForeground && GetFocusedWindow(_scanForeground) == _scanFocus;
        if ((!sameTarget || !SetImeEnglish(_scanForeground)) && !_imeWarningShown)
        {
            _imeWarningShown = true;
            BeginImeWarning(sameTarget ? null :
                "Input focus changed during scanning. English mode was not forced on the new target.");
        }
    }

    void BeginImeWarning(string? error = null)
    {
        Diagnostics.Write("WARNING", error ?? "Could not confirm English IME mode.");
        // Do not display a modal dialog inside the raw-input or hook callback.
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            MessageBox.Show(error ?? ("Could not confirm Microsoft Bopomofo switched to English. " +
                "The target may not support IMM control, or may be running as administrator. " +
                "Barcode interception remains active."), "ScannerGuard",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (_inputFailed) Application.ExitThread();
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
                Diagnostics.Fatal("keyboard-hook", ex);
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
        TrackShift(k, down);
        uint id = KeyId(k);
        if (!down && _passedDown.Remove(id))
        {
            return false;
        }
        bool held = !down && _swallowedDown.Remove(id);
        if (Environment.TickCount64 > _activeUntil && !held)
        {
            if (down)
            {
                _passedDown.Add(id);
                _passedKeys.RemoveAll(p => Environment.TickCount64 - p.Tick > 1000);
                if (_passedKeys.Count >= 4096)
                    throw new InvalidOperationException("Too many unmatched keyboard events; stopping input interception.");
                _passedKeys.Add(new PassedKey(k, _shiftMask, (GetKeyState(0x14) & 1) != 0, Environment.TickCount64));
            }
            return false;
        }
        _activeUntil = Environment.TickCount64 + ActiveGapMs;
        if (down) _swallowedDown.Add(id);
        if (down) EmitScannerKey(k, _shiftMask, (GetKeyState(0x14) & 1) != 0);
        ScheduleEnglish();
        return true;
    }

    void EmitScannerKey(KBDLLHOOKSTRUCT k, byte shift, bool capsLock)
    {
        uint vk = k.vkCode;
        if (IsShift(vk)) return;
        if (vk is 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x14) return;

        if (vk is 0x0D or 0x09) { _sendKeyPress((ushort)vk); return; }

        var state = new byte[256];
        if (shift != 0) state[0x10] = 0x80;
        if ((shift & 1) != 0) state[0xA0] = 0x80;
        if ((shift & 2) != 0) state[0xA1] = 0x80;
        if (capsLock) state[0x14] = 1;
        var sb = new System.Text.StringBuilder(4);
        if (ToUnicodeEx(vk, k.scanCode, state, sb, sb.Capacity, 4, _enUs) == 1) _sendCharacter(sb[0]);
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

    static void SendKeyRelease(ushort vk)
    {
        SendChecked(new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x2, dwExtraInfo = Marker } }
        });
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
