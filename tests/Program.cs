using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

internal static class RegressionTests
{
    [STAThread]
    static int Main(string[] args)
    {
        var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("RawInputWindow", true)!;
        using (var scanner = new Replay(type))
        {
            Check(!scanner.Key(0xA0, 0x2A, true, rawImmediately: false), "Initial Shift must initially pass.");
            scanner.Raw(0x10, 0x2A);
            Check(scanner.Releases.SequenceEqual(new ushort[] { 0xA0 }), "Passed Shift needs exactly one synthetic release.");
            Check(scanner.Shift == 1, "Synthetic release must not clear the scanner's physical Shift state.");
            Check(scanner.Key(0x4B, 0x25, true), "K must be swallowed during scanning.");
            Check(scanner.Key(0xA0, 0x2A, false), "Physical Shift release must be swallowed after synthetic release.");
            scanner.Key(0x4B, 0x25, false);
            Check(scanner.Text == (Replay.CapsLock ? "k" : "K"), "First letter lost its Shift state.");
            Check(scanner.Shift == 0, "Scanner Shift remained stuck.");
        }
        Console.WriteLine("PASS: leading Shift is neutralized once, stays usable for translation, then clears on physical release.");

        using (var scanner = new Replay(type))
        {
            scanner.Key(0xA0, 0x2A, true, rawImmediately: false);
            scanner.Key(0x4B, 0x25, true, rawImmediately: false);
            scanner.Key(0x48, 0x23, true, rawImmediately: false);
            scanner.Key(0xA0, 0x2A, false);
            scanner.Raw(0x10, 0x2A);
            scanner.Raw(0x4B, 0x25);
            scanner.Raw(0x48, 0x23);
            Check(scanner.Text == (Replay.CapsLock ? "kh" : "KH"), "Delayed Raw Input used post-release Shift state.");
            Check(scanner.Releases.Count == 0, "An already-released Shift must not be released again.");
            Check(scanner.Keys.Count(vk => vk == 0x08) == 2, "Both passed letters must be repaired.");
        }
        Console.WriteLine("PASS: delayed Raw Input preserves the key-down state for both initial letters.");

        using (var scanner = new Replay(type))
        {
            scanner.Key(0xA0, 0x2A, true);
            scanner.Key(0xA1, 0x36, true);
            scanner.Key(0xA0, 0x2A, false);
            scanner.Key(0x48, 0x23, true);
            Check(scanner.Text == (Replay.CapsLock ? "h" : "H"), "Releasing left Shift cleared held right Shift.");
            scanner.Key(0xA1, 0x36, false);
            Check(scanner.Shift == 0, "Right Shift remained stuck.");
        }
        Console.WriteLine("PASS: left/right Shift are independent.");

        using (var scanner = new Replay(type))
        {
            scanner.Key(0xA0, 0x2A, true);
            scanner.Expire();
            Check(scanner.Key(0xA0, 0x2A, false), "A neutralized Shift-up leaked after an idle gap.");
            Check(scanner.Shift == 0, "Expired scan left physical Shift held in the translator.");
        }
        Console.WriteLine("PASS: neutralized Shift release is swallowed even after the active gap expires.");

        using (var scanner = new Replay(type))
        {
            foreach (string value in new[] { "KH6TEST12", "KH6TEST12", "12345D2D678999E3F", "aZ09-_" })
            {
                scanner.Expire();
                int start = scanner.Text.Length;
                scanner.Type(value);
                Check(scanner.Text[start..] == value, "Repeated scan or case-sensitive translation failed.");
            }
            Check(scanner.Keys.Count(vk => vk == 0x0D) == 4, "Enter must be emitted once per scan.");
        }
        Console.WriteLine("PASS: consecutive letter-leading/digit-leading scans, original casing and Enter are preserved.");

        using (var scanner = new Replay(type))
        {
            scanner.Key(0xA0, 0x2A, true, rawImmediately: false);
            scanner.Raw(0x10, 0x2A, scanner: false);
            Check(!scanner.Key(0xA0, 0x2A, false), "An ordinary keyboard's Shift release must pass.");
            Check(scanner.Releases.Count == 0 && scanner.Text == "", "Ordinary keyboard input was modified.");
            scanner.Expire();
            scanner.Key(0x0D, 0x1C, true);
            Check(scanner.Keys.Count == 0, "Already-delivered Enter was duplicated.");
        }
        Console.WriteLine("PASS: ordinary keyboard Shift and already-passed Enter are not rewritten.");
        VerifyImeCompletion(type);

        if (args.Length > 1)
        {
            Check(!Replay.CapsLock, "Turn Caps Lock off before replaying the browser trace.");
            using var json = JsonDocument.Parse(File.ReadAllText(args[1]));
            using var scanner = new Replay(type);
            var expected = new StringBuilder();
            foreach (var item in json.RootElement.GetProperty("logs").EnumerateArray().OrderBy(e => e.GetProperty("seq").GetInt32()))
            {
                string code = item.GetProperty("code").GetString()!;
                bool down = item.GetProperty("type").GetString() == "keydown";
                ushort vk = code switch
                {
                    "ShiftLeft" => 0xA0,
                    "ShiftRight" => 0xA1,
                    "Enter" => 0x0D,
                    _ when code.StartsWith("Digit") || code.StartsWith("Key") => code[^1],
                    _ => throw new InvalidOperationException($"Unsupported trace code: {code}")
                };
                ushort scan = vk == 0xA0 ? (ushort)0x2A : vk == 0xA1 ? (ushort)0x36 : (ushort)MapVirtualKey(vk, 0);
                scanner.Key(vk, scan, down);
                string key = item.GetProperty("key").GetString()!;
                if (down && key.Length == 1) expected.Append(key);
            }
            Check(scanner.Text == expected.ToString(), "Actual browser trace translation differs from recorded key-down text.");
            Check(scanner.Shift == 0, "Actual browser trace left Shift held.");
            Console.WriteLine("PASS: private browser trace replays with exact original case and no held Shift.");
        }
        return 0;
    }

    static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    static void VerifyImeCompletion(Type type)
    {
        IntPtr previous = GetForegroundWindow();
        using var form = new Form { Text = "ScannerGuard isolated completion test", Width = 480, Height = 140 };
        using var input = new TextBox { Parent = form, Left = 12, Top = 12, Width = 400 };
        using var scanner = new Replay(type);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            form.Show();
            form.Activate();
            input.Focus();
            Application.DoEvents();
            Check(GetForegroundWindow() == form.Handle, "Test target did not gain focus; refusing IME test.");
            IntPtr ime = ImmGetDefaultIMEWnd(input.Handle);
            Check(ime != IntPtr.Zero, "Test input has no IME window.");
            Check(SendMessageTimeout(ime, 0x0283, (IntPtr)6, (IntPtr)1, 2, 100, out _) != IntPtr.Zero,
                "Could not open test IME.");
            Check(SendMessageTimeout(ime, 0x0283, (IntPtr)5, IntPtr.Zero, 2, 100, out var open) != IntPtr.Zero &&
                open != UIntPtr.Zero, "Test IME must be open before completion retry.");
            IntPtr layout = GetKeyboardLayout(0);
            object instance = scanner.Instance;
            type.GetField("_scanForeground", fields)!.SetValue(instance, form.Handle);
            type.GetField("_scanFocus", fields)!.SetValue(instance, input.Handle);
            type.GetMethod("ScheduleEnglish", fields)!.Invoke(instance, null);
            type.GetMethod("OnEnglishTimerTick", fields)!.Invoke(instance, new object?[] { null, EventArgs.Empty });
            Check(SendMessageTimeout(ime, 0x0283, (IntPtr)5, IntPtr.Zero, 2, 100, out open) != IntPtr.Zero &&
                open == UIntPtr.Zero, "Completion retry did not close the reopened IME.");
            Check(GetKeyboardLayout(0) == layout, "Completion retry changed keyboard layout.");
            Check(!scanner.Timer.Enabled, "Completion retry is still running.");
            SendMessageTimeout(ime, 0x0283, (IntPtr)6, (IntPtr)1, 2, 100, out _);
            var until = Environment.TickCount64 + 300;
            while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(10); }
            Check(SendMessageTimeout(ime, 0x0283, (IntPtr)5, IntPtr.Zero, 2, 100, out open) != IntPtr.Zero &&
                open != UIntPtr.Zero, "A later deliberate Chinese selection was overridden.");
            Console.WriteLine("PASS: finite completion retry closes a reopened IME, preserves layout and does not keep forcing English.");
        }
        finally
        {
            form.Close();
            if (previous != IntPtr.Zero) SetForegroundWindow(previous);
        }
    }

    sealed class Replay : IDisposable
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly Type _type, _keyType;
        readonly object _instance;
        readonly MethodInfo _route, _raw;
        readonly System.Windows.Forms.Timer _timer = new();
        readonly StringBuilder _text = new();
        bool _inScan;
        public readonly List<ushort> Releases = new(), Keys = new();
        public static bool CapsLock => (GetKeyState(0x14) & 1) != 0;
        public string Text => _text.ToString();
        public object Instance => _instance;
        public System.Windows.Forms.Timer Timer => _timer;
        public byte Shift => (byte)_type.GetField("_shiftMask", Fields)!.GetValue(_instance)!;

        public Replay(Type type)
        {
            _type = type;
            _keyType = type.GetNestedType("KBDLLHOOKSTRUCT", BindingFlags.NonPublic)!;
            _instance = RuntimeHelpers.GetUninitializedObject(type);
            Set("_passedDown", new HashSet<uint>());
            Set("_swallowedDown", new HashSet<uint>());
            Type passedType = type.GetNestedType("PassedKey", BindingFlags.NonPublic)!;
            Set("_passedKeys", Activator.CreateInstance(typeof(List<>).MakeGenericType(passedType))!);
            Set("_englishTimer", _timer);
            Set("_sendCharacter", (Action<char>)(c => _text.Append(c)));
            Set("_sendKeyPress", (Action<ushort>)(vk => Keys.Add(vk)));
            Set("_releaseKey", (Action<ushort>)(vk => Releases.Add(vk)));
            Set("_enUs", LoadKeyboardLayout("00000409", 0));
            _route = type.GetMethod("ShouldSwallow", Fields)!;
            _raw = type.GetMethod("HandlePassedScannerKey", Fields)!;
        }

        void Set(string name, object value) => _type.GetField(name, Fields)!.SetValue(_instance, value);

        public bool Key(ushort vk, ushort scan, bool down, bool rawImmediately = true)
        {
            if (_inScan) Set("_activeUntil", Environment.TickCount64 + 10000);
            object key = Activator.CreateInstance(_keyType)!;
            _keyType.GetField("vkCode")!.SetValue(key, (uint)vk);
            _keyType.GetField("scanCode")!.SetValue(key, (uint)scan);
            _keyType.GetField("flags")!.SetValue(key, down ? 0u : 0x80u);
            bool swallowed = (bool)_route.Invoke(_instance, new[] { key, (object)down })!;
            if (!swallowed && down && rawImmediately) Raw(vk, scan);
            return swallowed;
        }

        public void Raw(ushort vk, ushort scan, bool scanner = true)
        {
            // Routing/translation tests do not touch the user's IME, install hooks or send input.
            // The real timer's focus-guarded completion is covered by the separate IME test.
            Set("_activeUntil", Environment.TickCount64 + 10000);
            _raw.Invoke(_instance, new object[] { vk, scan, false, scanner });
            _inScan |= scanner;
        }

        public void Expire()
        {
            _inScan = false;
            Set("_activeUntil", 0L);
        }

        public void Type(string text)
        {
            foreach (char c in text)
            {
                short mapping = VkKeyScan(c);
                Check(mapping != -1, "Unsupported test character.");
                ushort vk = (ushort)(mapping & 255);
                bool shifted = ((mapping >> 8) & 1) != 0;
                if (char.IsLetter(c) && CapsLock) shifted = !shifted;
                if (shifted) Key(0xA0, 0x2A, true);
                ushort scan = (ushort)MapVirtualKey(vk, 0);
                Key(vk, scan, true);
                if (shifted) Key(0xA0, 0x2A, false);
                Key(vk, scan, false);
            }
            Key(0x0D, 0x1C, true);
            Key(0x0D, 0x1C, false);
        }

        public void Dispose() => _timer.Dispose();
    }

    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] static extern uint MapVirtualKey(uint code, uint kind);
    [DllImport("user32.dll", EntryPoint = "VkKeyScanW")] static extern short VkKeyScan(char c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadKeyboardLayout(string name, uint flags);
}
