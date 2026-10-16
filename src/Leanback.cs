using System;
using System.Collections.Generic;
using CefSharp.DevTools.Emulation;

namespace OmniTube;

/// <summary>
/// How we present ourselves to YouTube Leanback (youtube.com/tv). Leanback only serves
/// clients that look like a TV/console app, which run on Cobalt. Same approach as VacuumTube
/// (MIT): a Cobalt-format user agent plus matching client hints.
/// </summary>
internal static class Leanback
{
    private const string CobaltVersion = "27.lts.3.1040976-gold";
    private const string CobaltMajor = "27";
    private const string V8Version = "13.8.258.54";
    private const int StarboardVersion = 18;
    private const string Brand = "OmniTube";
    private const string AppId = "omnibox.omnitube";
    private const string AppVersion = "0.2.0";

    public static readonly string UserAgent =
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Cobalt/{CobaltVersion} (unlike Gecko) v8/{V8Version}-jit gles " +
        $"Starboard/{StarboardVersion}, {Brand}_DESKTOP_x8664_{DateTime.Now.Year}/{AppVersion} ({Brand}, Windows) {AppId}/{AppVersion}";

    private static string FullVersion
    {
        get
        {
            var parts = CobaltVersion.Split('-')[0].Split('.');
            return string.Join('.', parts, 0, Math.Min(3, parts.Length));
        }
    }

    public static UserAgentMetadata Metadata => new()
    {
        Brands = new List<UserAgentBrandVersion> { new() { Brand = Brand, Version = CobaltMajor } },
        FullVersionList = new List<UserAgentBrandVersion> { new() { Brand = Brand, Version = FullVersion } },
        FullVersion = FullVersion,
        Platform = "Starboard",
        PlatformVersion = "",
        Architecture = "x86",
        Model = "",
        Mobile = false,
        Bitness = "64",
        Wow64 = false,
    };

    public const string ExitMarker = "__OMNITUBE_EXIT__";

    /// <summary>VacuumTube's page modules, bundled by web/build.mjs and embedded into the exe.</summary>
    public static string LoadVacuumTubeBundle()
    {
        using var stream = typeof(Leanback).Assembly.GetManifestResourceStream("vacuumtube.bundle.js");
        if (stream == null) return null;
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Injected into every main-frame load:
    ///  - __omni(down, code): dispatches a Leanback key event (used by the native gamepad reader);
    ///  - exit: Leanback's "exit app" ends up either in window.close() or in an EXIT_APP signal
    ///    command — both are turned into a console marker the host listens for.
    /// </summary>
    public const string InjectScript = """
        (() => {
          if (window.__omniInstalled) return;
          window.__omniInstalled = true;

          // Native gamepad bridge. With VacuumTube's modules loaded, events go through its
          // controller pipeline (controller-support.js, settings overlay navigation);
          // otherwise they become Leanback key events directly.
          window.__omni = (down, code) => {
            const controller = window.__omniController;
            if (controller) {
              controller.emit(down ? 'down' : 'up', { code });
              return;
            }
            const e = new Event(down ? 'keydown' : 'keyup');
            e.keyCode = code;
            e.which = code;
            document.dispatchEvent(e);
          };

          const exit = () => console.log('__OMNITUBE_EXIT__');
          window.close = exit;

          const hook = setInterval(() => {
            if (window.__omniVtLoaded) { clearInterval(hook); return; } // VacuumTube's fix-exit handles it
            const yttv = window._yttv;
            if (!yttv) return;
            for (const key in yttv) {
              const inst = yttv[key] && yttv[key].instance;
              if (inst && typeof inst.resolveCommand === 'function') {
                const original = inst.resolveCommand;
                inst.resolveCommand = function (command) {
                  try {
                    const cmds = command && command.commandExecutorCommand && command.commandExecutorCommand.commands;
                    if (cmds && cmds.some(c => c && c.signalAction && c.signalAction.signal === 'EXIT_APP')) {
                      exit();
                      return true;
                    }
                  } catch (_) {}
                  return original.apply(this, arguments);
                };
                clearInterval(hook);
                return;
              }
            }
          }, 200);
        })();
        """;
}
