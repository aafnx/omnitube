using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OmniTube;

/// <summary>%APPDATA%\OmniTube\config.json — created with defaults on first start.</summary>
internal sealed class Config
{
    /// <summary>Page to open.</summary>
    public string Url { get; set; } = "https://www.youtube.com/tv";

    /// <summary>
    /// true  — Chromium hands frames over as a GPU texture (fast, recommended).
    /// false — frames are copied through system memory (slower, fallback).
    /// Switches to false automatically if the GPU path fails.
    /// </summary>
    public bool SharedTexture { get; set; } = true;

    /// <summary>Max rate at which Chromium produces frames.</summary>
    public int FrameRate { get; set; } = 60;

    /// <summary>Logical UI height; the page is laid out at this height and scaled to the screen.</summary>
    public int UiHeight { get; set; } = 1080;

    /// <summary>Open a 1280x720 window instead of borderless fullscreen (for debugging).</summary>
    public bool Windowed { get; set; } = false;

    /// <summary>Accept-Language, e.g. "ru-RU,ru,en". Empty = Windows UI language.</summary>
    public string Language { get; set; } = "";

    /// <summary>
    /// Load VacuumTube's page modules: ad block, SponsorBlock, DeArrow, dislikes, hide Shorts, etc.
    /// Their settings are inside YouTube: Settings -> VacuumTube.
    /// </summary>
    public bool VacuumTubeModules { get; set; } = true;

    /// <summary>
    /// Download new builds from the GitHub release in the background and install them on exit.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>GitHub repository with the "latest" release.</summary>
    public string UpdateRepo { get; set; } = "aafnx/omnitube";

    /// <summary>Hide the mouse cursor after this many seconds without movement (0 = never hide).</summary>
    public int HideCursorAfterSeconds { get; set; } = 3;

    /// <summary>Extra Chromium switches, e.g. "--disable-gpu-sandbox" or "--some-flag=value".</summary>
    public List<string> ExtraFlags { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string path)
    {
        Config config = null;
        try
        {
            if (File.Exists(path))
            {
                config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            Log.Error("config.json is invalid, using defaults", ex);
        }

        config ??= new Config();
        config.FrameRate = Math.Clamp(config.FrameRate, 24, 240);
        config.UiHeight = Math.Clamp(config.UiHeight, 480, 4320);
        config.ExtraFlags ??= new List<string>();
        config.Save(path); // writes defaults for any missing keys
        return config;
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save config.json", ex);
        }
    }
}
