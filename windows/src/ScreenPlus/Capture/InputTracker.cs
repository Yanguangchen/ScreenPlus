using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.Windows.WM;
using static TerraFX.Interop.Windows.VK;
using static TerraFX.Interop.Windows.WH;
using static TerraFX.Interop.Windows.PM;

namespace ScreenPlus.Capture;

/// <summary>
/// Logs mouse position, clicks and key presses with timestamps while recording.
///
/// Position is polled at 120 Hz; clicks and keys come from low-level input hooks on their own thread,
/// so a busy UI can never stall the system's input. Clicks on ScreenPlus's own windows (like the Stop
/// button) are left out. Only *when* a key was pressed and its rough kind are stored, never which key.
/// </summary>
internal sealed unsafe class InputTracker
{
    public enum Kind { Move, Click, Key }

    /// <summary>One input event. <c>Time</c> is on the QueryPerformanceCounter clock, in seconds; X/Y are physical pixels.</summary>
    public readonly record struct RawEvent(double Time, int X, int Y, Kind Kind, KeyKind Key = KeyKind.Regular);

    private static InputTracker? _active;

    private readonly List<RawEvent> _moves = [];
    private readonly List<RawEvent> _inputs = [];
    private readonly HashSet<uint> _keysDown = [];
    private readonly uint _processId = (uint)Environment.ProcessId;
    private Thread? _pollThread;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _running;

    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Start()
    {
        _moves.Clear();
        _inputs.Clear();
        _keysDown.Clear();
        _active = this;
        _running = true;

        _pollThread = new Thread(Poll) { IsBackground = true, Name = "ScreenPlus pointer", Priority = ThreadPriority.AboveNormal };
        _pollThread.Start();

        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() => RunHooks(ready)) { IsBackground = true, Name = "ScreenPlus input hooks" };
        _hookThread.Start();
        ready.Wait();
    }

    public List<RawEvent> Stop()
    {
        if (!_running) return [];
        _running = false;
        PostThreadMessageW(_hookThreadId, WM_QUIT, 0, 0);
        _hookThread?.Join();
        _pollThread?.Join();
        _active = null;
        return _moves.Concat(_inputs).OrderBy(e => e.Time).ToList();
    }

    /// <summary>Samples every tick, even when still, so interpolation never smears a move across a pause.</summary>
    private void Poll()
    {
        timeBeginPeriod(1);
        try
        {
            var interval = Stopwatch.Frequency / 120;
            var next = Stopwatch.GetTimestamp();
            while (_running)
            {
                if (Screens.CursorPosition() is var (x, y)) _moves.Add(new RawEvent(Now(), x, y, Kind.Move));
                next += interval;
                var wait = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -100) next = Stopwatch.GetTimestamp();  // fell far behind; don't try to catch up
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    private void RunHooks(ManualResetEventSlim ready)
    {
        _hookThreadId = GetCurrentThreadId();
        var module = GetModuleHandleW(null);
        var mouse = SetWindowsHookExW(WH_MOUSE_LL, &MouseProc, module, 0);
        var keyboard = SetWindowsHookExW(WH_KEYBOARD_LL, &KeyboardProc, module, 0);
        MSG message;
        // Creates this thread's message queue before Stop can post WM_QUIT to it.
        PeekMessageW(&message, HWND.NULL, 0, 0, PM_NOREMOVE);
        ready.Set();

        while (GetMessageW(&message, HWND.NULL, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (mouse != HHOOK.NULL) UnhookWindowsHookEx(mouse);
        if (keyboard != HHOOK.NULL) UnhookWindowsHookEx(keyboard);
    }

    // Hook callbacks run on the hook thread and must never throw (that would take down the process)
    // or take long (Windows silently removes slow hooks).

    [UnmanagedCallersOnly]
    private static LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (code >= 0 && _active is { } tracker && (uint)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
            {
                var info = (MSLLHOOKSTRUCT*)lParam;
                if (!tracker.IsOwnWindow(WindowFromPoint(info->pt)))
                    tracker._inputs.Add(new RawEvent(Now(), info->pt.x, info->pt.y, Kind.Click));
            }
        }
        catch (Exception)
        {
            // Losing one click is better than losing the recording.
        }
        return CallNextHookEx(HHOOK.NULL, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (code >= 0 && _active is { } tracker)
            {
                var info = (KBDLLHOOKSTRUCT*)lParam;
                var vk = info->vkCode;
                switch ((uint)wParam)
                {
                    case WM_KEYDOWN or WM_SYSKEYDOWN:
                        // Held keys auto-repeat; only the first press counts. Modifier keys alone make no sound.
                        if (tracker._keysDown.Add(vk) && !IsModifier(vk) && !tracker.IsOwnWindow(GetForegroundWindow()))
                        {
                            var (x, y) = Screens.CursorPosition() ?? (0, 0);
                            tracker._inputs.Add(new RawEvent(Now(), x, y, Kind.Key, KindOf(vk)));
                        }
                        break;
                    case WM_KEYUP or WM_SYSKEYUP:
                        tracker._keysDown.Remove(vk);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // Losing one key press is better than losing the recording.
        }
        return CallNextHookEx(HHOOK.NULL, code, wParam, lParam);
    }

    private bool IsOwnWindow(HWND window)
    {
        if (window == HWND.NULL) return false;
        uint processId;
        GetWindowThreadProcessId(window, &processId);
        return processId == _processId;
    }

    private static KeyKind KindOf(uint vk) => vk switch
    {
        VK_SPACE => KeyKind.Space,
        VK_RETURN => KeyKind.Enter,
        VK_BACK or VK_DELETE => KeyKind.Delete,
        _ => KeyKind.Regular,
    };

    private static bool IsModifier(uint vk) => vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT or VK_CONTROL or VK_LCONTROL
        or VK_RCONTROL or VK_MENU or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN or VK_CAPITAL;
}
