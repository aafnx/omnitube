using System;
using System.Threading;

namespace OmniTube;

/// <summary>
/// Polls XInput and turns controller input into the key codes YouTube Leanback understands
/// (Starboard gamepad codes: 32768 + W3C standard-gamepad button index), with key repeat —
/// the same scheme VacuumTube uses.
///
/// Reading the pad natively (instead of through the page's Gamepad API) means the Steam
/// overlay can block input to us while it is open: Steam filters XInput for the game process.
/// With Steam Input enabled, any controller (DualSense included) appears here as an Xbox pad.
/// </summary>
internal sealed class Gamepad : IDisposable
{
    private const int ButtonOffset = 32768;
    private const short StickThreshold = 16384;   // ~0.5 like VacuumTube
    private const byte TriggerThreshold = 30;
    private const int RepeatDelayMs = 500;
    private const int RepeatIntervalMs = 100;
    private const int PollIntervalMs = 8;

    // XInput button bit -> standard gamepad index
    private static readonly (ushort Mask, int Index)[] ButtonMap =
    {
        (0x1000, 0),  // A
        (0x2000, 1),  // B
        (0x4000, 2),  // X
        (0x8000, 3),  // Y
        (0x0100, 4),  // LB
        (0x0200, 5),  // RB
        (0x0020, 8),  // Back / Select
        (0x0010, 9),  // Start
        (0x0040, 10), // L3
        (0x0080, 11), // R3
        (0x0001, 12), // D-pad up
        (0x0002, 13), // D-pad down
        (0x0004, 14), // D-pad left
        (0x0008, 15), // D-pad right
    };

    private readonly Action<bool, int> _send;     // (down, keyCode)
    private readonly Func<bool> _isActive;
    private readonly bool[] _held = new bool[32];
    private Thread _thread;
    private volatile bool _running;

    private int _repeatCode = -1;
    private long _repeatAt;

    public Gamepad(Action<bool, int> send, Func<bool> isActive)
    {
        _send = send;
        _isActive = isActive;
    }

    public void Start()
    {
        try
        {
            Native.XInputGetState(0, out _);
        }
        catch (Exception ex)
        {
            Log.Error("XInput unavailable, controller support disabled", ex);
            return;
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Gamepad" };
        _thread.Start();
    }

    private void Loop()
    {
        var pressed = new bool[32];
        bool anyConnected = false;

        while (_running)
        {
            Array.Clear(pressed);
            bool connected = false;

            for (uint i = 0; i < 4; i++)
            {
                if (Native.XInputGetState(i, out var state) != Native.ERROR_SUCCESS) continue;
                connected = true;
                var g = state.Gamepad;

                foreach (var (mask, index) in ButtonMap)
                    if ((g.wButtons & mask) != 0) pressed[index] = true;

                if (g.bLeftTrigger > TriggerThreshold) pressed[6] = true;
                if (g.bRightTrigger > TriggerThreshold) pressed[7] = true;

                // sticks as virtual buttons 17..24 (XInput Y axis is positive-up)
                if (g.sThumbLY > StickThreshold) pressed[17] = true;
                if (g.sThumbLY < -StickThreshold) pressed[18] = true;
                if (g.sThumbLX < -StickThreshold) pressed[19] = true;
                if (g.sThumbLX > StickThreshold) pressed[20] = true;
                if (g.sThumbRY > StickThreshold) pressed[21] = true;
                if (g.sThumbRY < -StickThreshold) pressed[22] = true;
                if (g.sThumbRX < -StickThreshold) pressed[23] = true;
                if (g.sThumbRX > StickThreshold) pressed[24] = true;
            }

            if (connected != anyConnected)
            {
                anyConnected = connected;
                Log.Info(connected ? "Controller connected (XInput)" : "No XInput controller");
            }

            bool active = _isActive();
            long now = Environment.TickCount64;

            for (int b = 0; b < pressed.Length; b++)
            {
                bool down = pressed[b] && active;
                if (down == _held[b]) continue;
                _held[b] = down;

                int code = ButtonOffset + b;
                _send(down, code);

                if (down)
                {
                    _repeatCode = code;
                    _repeatAt = now + RepeatDelayMs;
                }
                else
                {
                    _repeatCode = -1;
                }
            }

            if (_repeatCode >= 0 && now >= _repeatAt)
            {
                _send(true, _repeatCode);
                _repeatAt = now + RepeatIntervalMs;
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
    }
}
