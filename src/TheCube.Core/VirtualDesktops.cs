namespace TheCube.Core;

/// <summary>One Windows virtual desktop.</summary>
/// <param name="Index">0-based position, left to right as in Task View.</param>
/// <param name="Id">Windows' GUID for the desktop.</param>
/// <param name="WallpaperPath">Per-desktop wallpaper (Windows 11), or null to use the system wallpaper.</param>
public sealed record DesktopInfo(int Index, Guid Id, string? WallpaperPath);

/// <summary>Access to Windows' own virtual desktops. Must be used from the UI (STA) thread.</summary>
public interface IVirtualDesktopService
{
    /// <summary>Human-readable name of the interface set in use, e.g. "Windows 11 24H2".</summary>
    string BackendName { get; }

    IReadOnlyList<DesktopInfo> GetDesktops();

    int GetCurrentIndex();

    /// <summary>Switch to desktop <paramref name="index"/>. <paramref name="animate"/> uses Windows' slide where available.</summary>
    void SwitchTo(int index, bool animate);

    /// <summary>Add a desktop at the end.</summary>
    void CreateDesktop();

    /// <summary>Which desktop a top-level window is on, or null if unknown or on all desktops.</summary>
    int? GetWindowDesktopIndex(nint hwnd);
}

/// <summary>Which set of undocumented virtual desktop interfaces a Windows build uses.</summary>
public enum DesktopApiVersion
{
    Unsupported,

    /// <summary>Windows 10 21H2/22H2 (builds 19044–19045).</summary>
    Windows10,

    /// <summary>Windows 11 22H2/23H2 (builds 22621–22631, updated since 2023-08).</summary>
    Windows11,

    /// <summary>Windows 11 24H2 and 25H2 (builds 26100 and later).</summary>
    Windows11_24H2,
}

public static class DesktopApiSelector
{
    public static DesktopApiVersion ForBuild(int build) => build switch
    {
        >= 19044 and <= 19045 => DesktopApiVersion.Windows10,
        >= 22621 and <= 22631 => DesktopApiVersion.Windows11,
        // Newer builds are tried with the newest layout; Windows rejects the interface IDs if they changed.
        >= 26100 => DesktopApiVersion.Windows11_24H2,
        _ => DesktopApiVersion.Unsupported,
    };

    public static string Describe(DesktopApiVersion version) => version switch
    {
        DesktopApiVersion.Windows10 => "Windows 10 21H2/22H2",
        DesktopApiVersion.Windows11 => "Windows 11 22H2/23H2",
        DesktopApiVersion.Windows11_24H2 => "Windows 11 24H2/25H2",
        _ => "unsupported",
    };
}

/// <summary>Thrown when this Windows build's virtual desktop interfaces aren't supported.</summary>
public sealed class VirtualDesktopsUnsupportedException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Desktop housekeeping shared by hotkeys and start-up.</summary>
public static class DesktopActions
{
    /// <summary>Add desktops until there are at least <paramref name="count"/>. Returns how many were added.</summary>
    public static int EnsureCount(IVirtualDesktopService desktops, int count)
    {
        var added = 0;
        var existing = desktops.GetDesktops().Count;
        while (existing + added < count)
        {
            desktops.CreateDesktop();
            added++;
        }

        return added;
    }

    /// <summary>Hotkey "switch to desktop N" (0-based). Does nothing if N doesn't exist or is already current.</summary>
    public static bool SwitchTo(IVirtualDesktopService desktops, int index, bool animate)
    {
        var count = desktops.GetDesktops().Count;
        if (index < 0 || index >= count || desktops.GetCurrentIndex() == index)
        {
            return false;
        }

        desktops.SwitchTo(index, animate);
        return true;
    }
}
