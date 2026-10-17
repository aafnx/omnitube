using System;
using System.Runtime.InteropServices;

namespace OmniTube;

/// <summary>
/// While a video is playing, tells Windows (and everything that listens to it — the Steam
/// Big Picture screensaver, Windows screensaver, display sleep) that the display is in use.
/// Same mechanism a video player or browser uses; it is released as soon as playback pauses
/// or stops, so the screensaver still works in menus and on pause.
///
/// Two APIs are used together, because different idle watchers look at different ones:
///  - a power request (PowerCreateRequest/PowerSetRequest, visible in `powercfg /requests`);
///  - the thread execution state (SetThreadExecutionState with ES_CONTINUOUS).
/// Must be called from one long-lived thread (the window thread): the execution state is per thread.
/// </summary>
internal static class KeepAwake
{
    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        // rest of the union (Detailed: HMODULE, ULONG, ULONG, LPWSTR*) — unused, kept for size
        private IntPtr _pad1;
        private IntPtr _pad2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr request, int type);

    private static IntPtr _request = IntPtr.Zero;
    private static IntPtr _reason = IntPtr.Zero;
    private static bool _active;

    public static bool Active => _active;

    public static void Set(bool playing)
    {
        if (playing == _active) return;
        _active = playing;

        try
        {
            EnsureRequest();
            if (playing)
            {
                if (_request != IntPtr.Zero)
                {
                    PowerSetRequest(_request, PowerRequestDisplayRequired);
                    PowerSetRequest(_request, PowerRequestSystemRequired);
                }
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED);
            }
            else
            {
                if (_request != IntPtr.Zero)
                {
                    PowerClearRequest(_request, PowerRequestDisplayRequired);
                    PowerClearRequest(_request, PowerRequestSystemRequired);
                }
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            }
            Log.Info(playing ? "Video playing: screensaver and display sleep held off" : "Video stopped: screensaver allowed");
        }
        catch (Exception ex)
        {
            Log.Warn("Keep-awake failed: " + ex.Message);
        }
    }

    /// <summary>dwExtraInfo of our synthetic input, so the window can ignore it.</summary>
    public static readonly IntPtr SyntheticInputTag = new(0x4F4D4E49); // "OMNI"

    private static long _pulses;

    /// <summary>
    /// Called every ~20 s while playing. Besides the power request, sends a zero-distance mouse
    /// move: it resets the system "last input" idle time, which is what idle timers that only
    /// count user input (like Steam's screensaver timer) look at. The pointer does not move.
    /// </summary>
    public static void Pulse()
    {
        if (!_active) return;

        Native.SetThreadExecutionState(Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED);

        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            mi = new Native.MOUSEINPUT { dx = 0, dy = 0, dwFlags = Native.MOUSEEVENTF_MOVE, dwExtraInfo = SyntheticInputTag },
        };
        uint sent = Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());

        _pulses++;
        if (sent != 1)
            Log.Warn($"SendInput failed: {Marshal.GetLastWin32Error()}");
        else if (_pulses == 1 || _pulses % 90 == 0)
            Log.Info($"Idle timer reset while playing (pulse #{_pulses})");
    }

    private static void EnsureRequest()
    {
        if (_request != IntPtr.Zero) return;

        _reason = Marshal.StringToHGlobalUni("OmniTube: playing video"); // lives for the whole process
        var context = new REASON_CONTEXT
        {
            Version = POWER_REQUEST_CONTEXT_VERSION,
            Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            SimpleReasonString = _reason,
        };
        IntPtr handle = PowerCreateRequest(ref context);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            Log.Warn("PowerCreateRequest failed: " + Marshal.GetLastWin32Error());
            return;
        }
        _request = handle;
    }
}
