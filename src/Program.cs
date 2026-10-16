using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.DevTools;
using CefSharp.OffScreen;
using static OmniTube.Native;

namespace OmniTube;

internal static class Program
{
    private const string WindowClass = "OmniTubeWindow";
    private const string Title = "OmniTube";
    private const uint TimerId = 1;
    private const uint WM_APP_FALLBACK = WM_APP + 1;
    private const uint WM_APP_PLAYBACK = WM_APP + 2;

    private static string _dataDir;
    private static string _configPath;
    private static Config _config;
    private static IntPtr _hwnd;
    private static D3DRenderer _renderer;
    private static OsrHandler _osr;
    private static ChromiumWebBrowser _browser;
    private static DevToolsClient _devTools;
    private static Gamepad _gamepad;
    private static string _acceptLanguage;

    private static IntPtr _arrowCursor;
    private static bool _cursorVisible = true;
    private static long _lastMouseMove;
    private static long _browserCreatedAt;
    private static bool _fallbackDone;
    private static long _lastAwakePulse;

    [STAThread]
    private static int Main(string[] args)
    {
        // Chromium child processes (renderer, GPU, ...) are this same exe.
        int subprocessExit = CefSharp.BrowserSubprocess.SelfHost.Main(args);
        if (subprocessExit >= 0) return subprocessExit;

        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniTube");
        Directory.CreateDirectory(_dataDir);
        Log.Init(Path.Combine(_dataDir, "omnitube.log"));
        Log.Info($"OmniTube {typeof(Program).Assembly.GetName().Version} starting, exe: {Environment.ProcessPath}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

        _configPath = Path.Combine(_dataDir, "config.json");
        _config = Config.Load(_configPath);
        Log.Info($"Config: sharedTexture={_config.SharedTexture} frameRate={_config.FrameRate} uiHeight={_config.UiHeight} windowed={_config.Windowed} flags=[{string.Join(' ', _config.ExtraFlags)}]");

        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        Updater.Init(_dataDir, _config.UpdateRepo);

        using var mutex = new Mutex(true, @"Local\OmniTube.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            Log.Info("Already running, focusing existing window");
            IntPtr existing = FindWindowW(WindowClass, null);
            if (existing != IntPtr.Zero) SetForegroundWindow(existing);
            return 0;
        }

        try
        {
            Updater.CollectOldFiles();
            return Run();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            return 1;
        }
    }

    private static int Run()
    {
        if (!InitializeCef()) return 1;

        CreateMainWindow(out int width, out int height);

        _renderer = new D3DRenderer(_hwnd, width, height);
        _renderer.Start();

        _osr = new OsrHandler(_renderer, _config.UiHeight, width, height);
        Log.Info($"View {_osr.DipWidth}x{_osr.DipHeight} @ scale {_osr.Scale:0.###}");

        CreateBrowser(_config.SharedTexture);

        _gamepad = new Gamepad(SendGamepadKey, () => GetForegroundWindow() == _hwnd);
        _gamepad.Start();

        if (_config.AutoUpdate)
            Updater.StartBackgroundChecks(_ => ShowToast(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
                ? "OmniTube: обновление загружено, установится после выхода"
                : "OmniTube: update downloaded, it will be installed when you exit"));
        else
            Log.Info("Auto-update disabled in config.json");

        SetTimer(_hwnd, (IntPtr)TimerId, 1000, IntPtr.Zero);

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        Log.Info("Shutting down");
        _gamepad?.Dispose();
        _devTools?.Dispose();
        _browser?.Dispose();
        Cef.Shutdown();
        _renderer?.Dispose();
        if (_config.AutoUpdate) Updater.ApplyStagedUpdate();
        Log.Info("Bye");
        return 0;
    }

    // ------------------------------------------------------------------ CEF

    private static bool InitializeCef()
    {
        var culture = CultureInfo.CurrentUICulture;
        string lang = culture.TwoLetterISOLanguageName;
        string locale = lang == "en" ? "en-US" : lang;
        _acceptLanguage = string.IsNullOrWhiteSpace(_config.Language)
            ? (culture.Name.Length > 0 && culture.Name != lang ? $"{culture.Name},{lang},en-US,en" : $"{lang},en-US,en")
            : _config.Language;

        var settings = new CefSettings
        {
            RootCachePath = Path.Combine(_dataDir, "cef"),
            CachePath = Path.Combine(_dataDir, "cef", "profile"),
            LogFile = Path.Combine(_dataDir, "cef.log"),
            LogSeverity = LogSeverity.Warning,
            UserAgent = Leanback.UserAgent,
            Locale = locale,
            AcceptLanguageList = _acceptLanguage,
            PersistSessionCookies = true,
            BrowserSubprocessPath = Environment.ProcessPath,
            MultiThreadedMessageLoop = true,
        };
        settings.EnableAudio();

        settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";

        foreach (string raw in _config.ExtraFlags)
        {
            string flag = raw?.Trim();
            if (string.IsNullOrEmpty(flag)) continue;
            flag = flag.TrimStart('-');
            int eq = flag.IndexOf('=');
            string name = eq >= 0 ? flag[..eq] : flag;
            string value = eq >= 0 ? flag[(eq + 1)..] : string.Empty;
            settings.CefCommandLineArgs[name] = value;
        }

        Log.Info($"CEF {Cef.CefVersion}, Chromium {Cef.ChromiumVersion}, locale {locale}, accept-language {_acceptLanguage}");
        Log.Info("User agent: " + Leanback.UserAgent);

        if (!Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null))
        {
            Log.Error("Cef.Initialize failed (see cef.log)");
            return false;
        }

        return true;
    }

    private static void CreateBrowser(bool sharedTexture)
    {
        Log.Info($"Creating browser, sharedTexture={sharedTexture}");

        var browserSettings = new BrowserSettings(autoDispose: true)
        {
            WindowlessFrameRate = _config.FrameRate,
            BackgroundColor = 0xFF000000,
        };

        var browser = new ChromiumWebBrowser("about:blank", null, null,
            automaticallyCreateBrowser: false, onAfterBrowserCreated: null, useLegacyRenderHandler: false);
        browser.RenderHandler = _osr;
        browser.ConsoleMessage += OnConsoleMessage;
        browser.FrameLoadEnd += OnFrameLoadEnd;
        browser.LoadError += (_, e) =>
        {
            if (e.Frame.IsMain && e.ErrorCode != CefErrorCode.Aborted)
                Log.Warn($"Load error {e.ErrorCode} for {e.FailedUrl}: {e.ErrorText}");
        };

        var windowInfo = new WindowInfo();
        windowInfo.SetAsWindowless(_hwnd);
        windowInfo.SharedTextureEnabled = sharedTexture;

        _browser = browser;
        _browserCreatedAt = Environment.TickCount64;

        browser.CreateBrowserAsync(windowInfo, browserSettings).ContinueWith(async t =>
        {
            if (t.IsFaulted)
            {
                Log.Error("Browser creation failed", t.Exception);
                return;
            }

            try
            {
                var host = t.Result.GetHost();
                host.WasHidden(false);
                host.SetFocus(true);

                // Cobalt client hints for navigator.userAgentData / Sec-CH-UA (like VacuumTube)
                _devTools = browser.GetDevToolsClient();
                await _devTools.Emulation.SetUserAgentOverrideAsync(Leanback.UserAgent, _acceptLanguage, null, Leanback.Metadata);
                Log.Info("User agent override applied");
            }
            catch (Exception ex)
            {
                Log.Warn("User agent override failed, continuing with plain UA: " + ex.Message);
            }

            if (_devTools != null) await InstallPageScriptsAsync();

            Log.Info("Loading " + _config.Url);
            browser.Load(_config.Url);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Registers our page script (gamepad bridge, exit hook) plus VacuumTube's modules (ad block,
    /// SponsorBlock, DeArrow, ...) to run in every new document before YouTube's own scripts.
    /// CSP is bypassed so those modules can reach their APIs (sponsor.ajay.app etc.), as VacuumTube does.
    /// </summary>
    private static async Task InstallPageScriptsAsync()
    {
        try
        {
            await _devTools.Page.EnableAsync();
            await _devTools.Page.SetBypassCSPAsync(true);

            string script = Leanback.InjectScript;
            if (_config.VacuumTubeModules)
            {
                string bundle = Leanback.LoadVacuumTubeBundle();
                if (bundle != null)
                {
                    script += "\n;\n" + bundle;
                    Log.Info($"VacuumTube modules: {bundle.Length / 1024} KB");
                }
                else
                {
                    Log.Warn("VacuumTube bundle is missing from this build");
                }
            }
            else
            {
                Log.Info("VacuumTube modules disabled in config.json");
            }

            await _devTools.Page.AddScriptToEvaluateOnNewDocumentAsync(script);
            Log.Info("Page scripts installed");
        }
        catch (Exception ex)
        {
            Log.Warn("Installing page scripts failed: " + ex.Message);
        }
    }

    private static void FallbackToCpuFrames()
    {
        if (_fallbackDone) return;
        _fallbackDone = true;

        Log.Warn($"GPU texture path produced no frames (failures: {_renderer.SharedTextureFailures}). " +
                 "Switching to CPU frames and saving sharedTexture=false to config.json.");
        _config.SharedTexture = false;
        _config.Save(_configPath);

        var old = _browser;
        _browser = null;
        _devTools?.Dispose();
        _devTools = null;
        old?.Dispose();

        CreateBrowser(sharedTexture: false);
    }

    private static void OnConsoleMessage(object sender, ConsoleMessageEventArgs e)
    {
        if (e.Message == null) return;

        if (e.Message.Contains(Leanback.ExitMarker))
        {
            Log.Info("Exit requested by YouTube");
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        else if (e.Message.StartsWith(Leanback.PlaybackMarker, StringComparison.Ordinal))
        {
            bool playing = e.Message.EndsWith("1", StringComparison.Ordinal);
            PostMessageW(_hwnd, WM_APP_PLAYBACK, playing ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        }
        else if (e.Message.StartsWith("[omnitube]", StringComparison.Ordinal))
        {
            Log.Info("page: " + e.Message);
        }
    }

    private static void OnFrameLoadEnd(object sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain) return;
        Log.Info($"Loaded {e.Url} (HTTP {e.HttpStatusCode})");
        e.Frame.ExecuteJavaScriptAsync(Leanback.InjectScript);
    }

    /// <summary>Small notice in the corner of the page, OMNIBOX colours.</summary>
    private static void ShowToast(string message)
    {
        string text = System.Text.Json.JsonSerializer.Serialize(message);
        RunInPage("(() => { const d = document.createElement('div'); d.textContent = " + text + "; " +
                  "Object.assign(d.style, { position: 'fixed', right: '48px', bottom: '48px', zIndex: '2147483647', " +
                  "padding: '18px 28px', borderRadius: '14px', background: 'rgba(10,12,31,0.92)', color: '#DEE4EE', " +
                  "font: '500 22px/1.3 Roboto, Arial, sans-serif', border: '1px solid rgba(124,108,255,0.6)', " +
                  "boxShadow: '0 8px 32px rgba(0,0,0,0.5)', transition: 'opacity 0.4s' }); " +
                  "document.body.appendChild(d); " +
                  "setTimeout(() => { d.style.opacity = '0'; setTimeout(() => d.remove(), 500); }, 8000); })()");
    }

    private static void SendGamepadKey(bool down, int code) =>
        RunInPage($"window.__omni && window.__omni({(down ? "true" : "false")}, {code})");

    private static void RunInPage(string js)
    {
        var browser = _browser;
        if (browser == null || !browser.IsBrowserInitialized) return;
        try
        {
            browser.GetBrowser()?.MainFrame?.ExecuteJavaScriptAsync(js);
        }
        catch (Exception ex)
        {
            Log.Warn("Page call failed: " + ex.Message);
        }
    }

    private static IBrowserHost Host
    {
        get
        {
            var browser = _browser;
            if (browser == null || !browser.IsBrowserInitialized) return null;
            try { return browser.GetBrowser()?.GetHost(); }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ window

    private static unsafe void CreateMainWindow(out int width, out int height)
    {
        IntPtr instance = GetModuleHandleW(null);
        _arrowCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW);

        IntPtr className = Marshal.StringToHGlobalUni(WindowClass); // lives for the whole process
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = instance,
            hIcon = LoadIconW(instance, (IntPtr)32512), // ApplicationIcon (Roslyn embeds it as icon group 32512)
            hIconSm = LoadIconW(instance, (IntPtr)32512),
            hCursor = IntPtr.Zero, // managed in WM_SETCURSOR
            lpszClassName = className,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassExW failed: " + Marshal.GetLastWin32Error());

        int x, y;
        uint style;
        if (_config.Windowed)
        {
            x = 100; y = 100; width = 1280; height = 720;
            style = WS_OVERLAPPEDWINDOW;
        }
        else
        {
            var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            GetMonitorInfoW(monitor, ref info);
            x = info.rcMonitor.Left;
            y = info.rcMonitor.Top;
            width = info.rcMonitor.Right - info.rcMonitor.Left;
            height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            style = WS_POPUP;
        }

        _hwnd = CreateWindowExW(WS_EX_APPWINDOW, WindowClass, Title, style, x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowExW failed: " + Marshal.GetLastWin32Error());

        GetClientRect(_hwnd, out RECT rc);
        width = Math.Max(1, rc.Right - rc.Left);
        height = Math.Max(1, rc.Bottom - rc.Top);

        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        Log.Info($"Window {width}x{height} at {x},{y} ({(_config.Windowed ? "windowed" : "borderless fullscreen")})");
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            IntPtr? handled = HandleMessage(hwnd, msg, wParam, lParam);
            if (handled.HasValue) return handled.Value;
        }
        catch (Exception ex)
        {
            Log.Error($"WndProc 0x{msg:x4}", ex);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return (IntPtr)1;

            case WM_SIZE:
            {
                int w = (int)((long)lParam & 0xFFFF);
                int h = (int)(((long)lParam >> 16) & 0xFFFF);
                if (w > 0 && h > 0 && _renderer != null && _osr != null)
                {
                    _renderer.RequestResize(w, h);
                    _osr.SetPixelSize(w, h);
                    var host = Host;
                    host?.NotifyScreenInfoChanged();
                    host?.WasResized();
                }
                return IntPtr.Zero;
            }

            case WM_SETFOCUS:
                Host?.SetFocus(true);
                return IntPtr.Zero;

            case WM_ACTIVATEAPP:
                // VacuumTube's "pause on blur" listens for these
                RunInPage($"window.__omniIpcEmit && window.__omniIpcEmit('{(wParam != IntPtr.Zero ? "focus" : "blur")}')");
                return IntPtr.Zero;

            case WM_SETCURSOR:
                if (LoWord(lParam) == HTCLIENT)
                {
                    SetCursor(_cursorVisible ? _arrowCursor : IntPtr.Zero);
                    return (IntPtr)1;
                }
                return null;

            case WM_TIMER:
                OnTimer();
                return IntPtr.Zero;

            case WM_APP_FALLBACK:
                FallbackToCpuFrames();
                return IntPtr.Zero;

            case WM_APP_PLAYBACK:
                if (_config.KeepAwakeWhilePlaying) KeepAwake.Set(wParam != IntPtr.Zero);
                return IntPtr.Zero;

            case WM_KEYDOWN:
            case WM_KEYUP:
            case WM_CHAR:
                Host?.SendKeyEvent((int)msg, (int)(long)wParam, (int)(long)lParam);
                return IntPtr.Zero;

            case WM_SYSKEYDOWN:
            case WM_SYSKEYUP:
            case WM_SYSCHAR:
                Host?.SendKeyEvent((int)msg, (int)(long)wParam, (int)(long)lParam);
                return null; // let DefWindowProc handle Alt+F4

            case WM_MOUSEMOVE:
                ShowCursorNow();
                Host?.SendMouseMoveEvent(MouseAt(lParam), false);
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
            case WM_LBUTTONUP:
            case WM_RBUTTONDOWN:
            case WM_RBUTTONUP:
            case WM_MBUTTONDOWN:
            case WM_MBUTTONUP:
            {
                ShowCursorNow();
                var button = msg is WM_LBUTTONDOWN or WM_LBUTTONUP ? MouseButtonType.Left
                    : msg is WM_RBUTTONDOWN or WM_RBUTTONUP ? MouseButtonType.Right
                    : MouseButtonType.Middle;
                bool up = msg is WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP;
                Host?.SendMouseClickEvent(MouseAt(lParam), button, up, 1);
                return IntPtr.Zero;
            }

            case WM_MOUSEWHEEL:
            {
                ShowCursorNow();
                var pt = new POINT { X = LoWord(lParam), Y = HiWord(lParam) };
                ScreenToClient(hwnd, ref pt);
                float s = _osr?.Scale ?? 1f;
                Host?.SendMouseWheelEvent(new MouseEvent((int)(pt.X / s), (int)(pt.Y / s), CefEventFlags.None), 0, HiWord(wParam));
                return IntPtr.Zero;
            }

            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return null;
    }

    private static MouseEvent MouseAt(IntPtr lParam)
    {
        float s = _osr?.Scale ?? 1f;
        return new MouseEvent((int)(LoWord(lParam) / s), (int)(HiWord(lParam) / s), CefEventFlags.None);
    }

    private static void ShowCursorNow()
    {
        _lastMouseMove = Environment.TickCount64;
        if (!_cursorVisible)
        {
            _cursorVisible = true;
            SetCursor(_arrowCursor);
        }
    }

    private static void OnTimer()
    {
        long now = Environment.TickCount64;

        if (now - _lastAwakePulse > 30_000)
        {
            _lastAwakePulse = now;
            KeepAwake.Pulse();
        }

        if (_config.HideCursorAfterSeconds > 0 && _cursorVisible && now - _lastMouseMove > _config.HideCursorAfterSeconds * 1000L)
        {
            _cursorVisible = false;
            SetCursor(IntPtr.Zero);
        }

        // GPU texture path watchdog: no frame ever, or the handles cannot be opened -> CPU frames.
        if (_config.SharedTexture && !_fallbackDone && _renderer != null && Interlocked.Read(ref _renderer.FramesReceived) == 0)
        {
            bool tooManyFailures = _renderer.SharedTextureFailures >= 10;
            bool timedOut = now - _browserCreatedAt > 10_000;
            if (tooManyFailures || timedOut)
                PostMessageW(_hwnd, WM_APP_FALLBACK, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
