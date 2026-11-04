using System.Runtime.InteropServices;
using TheCube.Core;

namespace TheCube.App.Interop;

/// <summary>
/// <see cref="IVirtualDesktopService"/> over Windows' own virtual desktops. Picks the interface set
/// for the running build, and reconnects once if Explorer has restarted since the last call.
/// </summary>
internal sealed class WindowsVirtualDesktops : IVirtualDesktopService
{
    private const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    private const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
    private const int RPC_S_CALL_FAILED = unchecked((int)0x800706BE);

    private readonly DesktopApiVersion _version;
    private IDesktopApi _api;
    private IVirtualDesktopManager _manager;

    private WindowsVirtualDesktops(DesktopApiVersion version, int build, int revision)
    {
        _version = version;
        Build = build;
        Revision = revision;
        (_api, _manager) = Connect(version);
    }

    public int Build { get; }
    public int Revision { get; }

    public string BackendName => DesktopApiSelector.Describe(_version);

    /// <summary>Connect for the running Windows build. Throws <see cref="VirtualDesktopsUnsupportedException"/>.</summary>
    public static WindowsVirtualDesktops Create()
    {
        var (build, revision) = NativeMethods.GetWindowsBuild();
        var version = DesktopApiSelector.ForBuild(build);
        if (version == DesktopApiVersion.Unsupported)
        {
            throw new VirtualDesktopsUnsupportedException(
                $"Windows build {build}.{revision} isn't supported. The Cube supports Windows 10 22H2 (19045) " +
                "and Windows 11 23H2 (22631), 24H2 and 25H2 (26100+).");
        }

        try
        {
            return new WindowsVirtualDesktops(version, build, revision);
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            throw new VirtualDesktopsUnsupportedException(
                $"Windows build {build}.{revision} changed its virtual desktop interfaces, so The Cube can't use them yet " +
                $"(tried {DesktopApiSelector.Describe(version)}: {ex.Message}).", ex);
        }
    }

    private static (IDesktopApi, IVirtualDesktopManager) Connect(DesktopApiVersion version)
    {
        var shellType = Type.GetTypeFromCLSID(ShellGuids.CLSID_ImmersiveShell, throwOnError: true)!;
        var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;
        IDesktopApi api = version switch
        {
            DesktopApiVersion.Windows10 => Win10.Api.Connect(shell),
            DesktopApiVersion.Windows11 => Win11.Api23H2.Connect(shell),
            DesktopApiVersion.Windows11_24H2 => Win11.Api24H2.Connect(shell),
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };
        var managerType = Type.GetTypeFromCLSID(ShellGuids.CLSID_VirtualDesktopManager, throwOnError: true)!;
        var manager = (IVirtualDesktopManager)Activator.CreateInstance(managerType)!;

        // Make one harmless call so a wrong vtable layout fails here, not mid-switch.
        _ = api.GetCount();
        return (api, manager);
    }

    private T Call<T>(Func<IDesktopApi, T> action)
    {
        try
        {
            return action(_api);
        }
        catch (COMException ex) when (ex.HResult is RPC_E_DISCONNECTED or RPC_S_SERVER_UNAVAILABLE or RPC_S_CALL_FAILED)
        {
            // Explorer restarted: its COM objects are gone. Reconnect once and retry.
            (_api, _manager) = Connect(_version);
            return action(_api);
        }
    }

    public IReadOnlyList<DesktopInfo> GetDesktops() => Call(api =>
        api.GetDesktops()
            .Select((d, i) => new DesktopInfo(i, api.GetId(d), api.GetWallpaper(d)))
            .ToList());

    /// <summary>
    /// Desktop of each window in one pass. Null for windows Windows doesn't track; -1 for windows whose
    /// desktop ID isn't one of the desktops (pinned to all desktops).
    /// </summary>
    internal Dictionary<nint, int?> GetWindowDesktops(IEnumerable<nint> windows, IReadOnlyList<DesktopInfo> desktops)
    {
        var byId = desktops.ToDictionary(d => d.Id, d => d.Index);
        var result = new Dictionary<nint, int?>();
        foreach (var hwnd in windows)
        {
            try
            {
                var id = _manager.GetWindowDesktopId(hwnd);
                result[hwnd] = id == Guid.Empty ? null : byId.TryGetValue(id, out var index) ? index : -1;
            }
            catch (COMException)
            {
                result[hwnd] = null;
            }
        }

        return result;
    }

    public int GetCurrentIndex() => Call(api =>
    {
        var current = api.GetId(api.GetCurrent());
        var desktops = api.GetDesktops();
        for (var i = 0; i < desktops.Count; i++)
        {
            if (api.GetId(desktops[i]) == current)
            {
                return i;
            }
        }

        return 0;
    });

    public void SwitchTo(int index, bool animate)
    {
        Call(api =>
        {
            var desktops = api.GetDesktops();
            if (index < 0 || index >= desktops.Count)
            {
                return false;
            }

            api.Switch(desktops[index], animate);
            return true;
        });
        FocusTopWindowOnCurrentDesktop();
    }

    public void CreateDesktop() => Call(api =>
    {
        api.Create();
        return true;
    });

    public int? GetWindowDesktopIndex(nint hwnd)
    {
        Guid id;
        try
        {
            id = _manager.GetWindowDesktopId(hwnd);
        }
        catch (COMException)
        {
            return null; // not a top-level window, or one the shell doesn't track
        }

        if (id == Guid.Empty)
        {
            return null;
        }

        var desktops = GetDesktops();
        var match = desktops.FirstOrDefault(d => d.Id == id);
        return match?.Index;
    }

    /// <summary>
    /// After a switch, give focus to the top window on the new desktop, as Plasma does. Without this
    /// the focus can stay on a window that's now on another desktop, and its taskbar button flashes.
    /// </summary>
    private void FocusTopWindowOnCurrentDesktop()
    {
        nint target = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsSwitchableWindow(hwnd))
            {
                return true;
            }

            try
            {
                if (!_manager.IsWindowOnCurrentVirtualDesktop(hwnd))
                {
                    return true;
                }
            }
            catch (COMException)
            {
                return true;
            }

            target = hwnd;
            return false; // EnumWindows goes top to bottom, so the first match is the top window
        }, 0);

        if (target != 0)
        {
            NativeMethods.SetForegroundWindow(target);
        }
    }

    /// <summary>A window that would appear in Alt+Tab.</summary>
    internal static bool IsSwitchableWindow(nint hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd) || NativeMethods.IsCloaked(hwnd))
        {
            return false;
        }

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_APPWINDOW) != 0)
        {
            return true;
        }

        const uint GW_OWNER = 4;
        return (exStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) == 0
            && NativeMethods.GetWindow(hwnd, GW_OWNER) == 0
            && NativeMethods.GetWindowTextLength(hwnd) > 0;
    }
}
