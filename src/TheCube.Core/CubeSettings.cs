using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheCube.Core;

/// <summary>What fills the space behind the cube.</summary>
public enum CubeBackground
{
    Color,
    SkyBox,
}

/// <summary>A screen edge that can open the cube when the pointer hits it.</summary>
public enum ScreenEdge
{
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    TopLeft,
}

/// <summary>
/// User settings. Defaults match the KDE Plasma cube set-up this app mirrors:
/// KWin cube defaults, 4 desktops in one row, Meta+C to toggle, Ctrl+F1..F4 to switch,
/// and the 2.83x animation speed factor (566 ms long animations).
/// </summary>
public sealed class CubeSettings
{
    /// <summary>Extra gap between the cube's centre and each face, in pixels.</summary>
    public double FaceDisplacement { get; set; } = 100;

    /// <summary>How far the camera pulls back when the cube is open, as a multiple of the face distance.</summary>
    public double DistanceFactor { get; set; } = 1.75;

    public bool MouseInvertedX { get; set; }
    public bool MouseInvertedY { get; set; }

    public CubeBackground Background { get; set; } = CubeBackground.Color;

    /// <summary>Background colour as #RRGGBB.</summary>
    public string BackgroundColor { get; set; } = "#212427";

    /// <summary>Path to a skybox image, used when <see cref="Background"/> is SkyBox.</summary>
    public string? SkyBox { get; set; }

    /// <summary>Screen edges that open the cube. None by default.</summary>
    public List<ScreenEdge> BorderActivate { get; set; } = [];

    /// <summary>Duration of the open/close/rotate animations.</summary>
    public int AnimationDurationMs { get; set; } = 566;

    /// <summary>Create Windows virtual desktops at start-up until there are at least this many (0 = leave alone).</summary>
    public int EnsureDesktopCount { get; set; } = 4;

    /// <summary>Start The Cube when you sign in to Windows (like an always-on KWin effect).</summary>
    public bool StartWithWindows { get; set; } = true;

    public string ToggleCubeHotkey { get; set; } = "Win+C";

    /// <summary>Hotkeys that jump straight to desktop 1, 2, 3, ... Empty strings are unbound.</summary>
    public List<string> SwitchToDesktopHotkeys { get; set; } = ["Ctrl+F1", "Ctrl+F2", "Ctrl+F3", "Ctrl+F4"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CubeSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CubeSettings();
        }

        var settings = JsonSerializer.Deserialize<CubeSettings>(File.ReadAllText(path), JsonOptions)
            ?? new CubeSettings();
        settings.Normalize();
        return settings;
    }

    /// <summary>Deep copy (for editing in a dialog that can be cancelled).</summary>
    public CubeSettings Clone() =>
        JsonSerializer.Deserialize<CubeSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Clamp out-of-range values from a hand-edited file to something usable.</summary>
    public void Normalize()
    {
        if (!double.IsFinite(FaceDisplacement)) FaceDisplacement = 100;
        if (!double.IsFinite(DistanceFactor) || DistanceFactor < 1) DistanceFactor = 1.75;
        AnimationDurationMs = Math.Clamp(AnimationDurationMs, 0, 5000);
        EnsureDesktopCount = Math.Clamp(EnsureDesktopCount, 0, 20);
        if (!TryParseColor(BackgroundColor, out _)) BackgroundColor = "#212427";
        BorderActivate ??= [];
        SwitchToDesktopHotkeys ??= [];
        ToggleCubeHotkey ??= "";
    }

    public static bool TryParseColor(string? text, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (text is not { Length: 7 } || text[0] != '#')
        {
            return false;
        }

        if (!uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            return false;
        }

        rgb = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}
