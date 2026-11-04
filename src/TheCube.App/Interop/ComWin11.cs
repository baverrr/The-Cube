using System.Runtime.InteropServices;

namespace TheCube.App.Interop.Win11;

// Windows 11 22H2/23H2 (builds 22621–22631, after the 2023-08 update) and 24H2/25H2 (26100+).
// Both use the same interface IDs, but 24H2 inserted SwitchDesktopAndMoveForegroundView after
// SwitchDesktop, shifting every later method, so each gets its own declaration.

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsViewVisible(nint view);

    Guid GetId();

    /// <summary>HSTRING.</summary>
    nint GetName();

    /// <summary>HSTRING.</summary>
    nint GetWallpaperPath();

    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsRemote();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal23H2
{
    int GetCount();
    void MoveViewToDesktop(nint view, IVirtualDesktop desktop);
    [return: MarshalAs(UnmanagedType.Bool)]
    bool CanViewMoveDesktops(nint view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig]
    int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int index);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray unknown1, out IObjectArray unknown2);
    void SetDesktopName(IVirtualDesktop desktop, nint name);
    void SetDesktopWallpaper(IVirtualDesktop desktop, nint path);
    void UpdateWallpaperPathForAllDesktops(nint path);
    void CopyDesktopState(nint view0, nint view1);
    void CreateRemoteDesktop(nint path, out IVirtualDesktop desktop);
    void SwitchRemoteDesktop(IVirtualDesktop desktop, nint switchType);
    void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
    void GetLastActiveDesktop(out IVirtualDesktop desktop);
    void WaitForAnimationToComplete();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal24H2
{
    int GetCount();
    void MoveViewToDesktop(nint view, IVirtualDesktop desktop);
    [return: MarshalAs(UnmanagedType.Bool)]
    bool CanViewMoveDesktops(nint view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig]
    int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop);
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int index);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray unknown1, out IObjectArray unknown2);
    void SetDesktopName(IVirtualDesktop desktop, nint name);
    void SetDesktopWallpaper(IVirtualDesktop desktop, nint path);
    void UpdateWallpaperPathForAllDesktops(nint path);
    void CopyDesktopState(nint view0, nint view1);
    void CreateRemoteDesktop(nint path, out IVirtualDesktop desktop);
    void SwitchRemoteDesktop(IVirtualDesktop desktop, nint switchType);
    void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
    void GetLastActiveDesktop(out IVirtualDesktop desktop);
    void WaitForAnimationToComplete();
}

internal abstract class ApiBase : IDesktopApi
{
    public abstract int GetCount();
    public abstract object GetCurrent();
    public abstract void Switch(object desktop, bool animate);
    public abstract void Create();
    protected abstract IObjectArray Desktops();

    public IReadOnlyList<object> GetDesktops() => ObjectArrays.ToList(Desktops(), typeof(IVirtualDesktop).GUID);

    public Guid GetId(object desktop) => ((IVirtualDesktop)desktop).GetId();

    public string? GetWallpaper(object desktop)
    {
        var path = NativeMethods.TakeHString(((IVirtualDesktop)desktop).GetWallpaperPath());
        return string.IsNullOrEmpty(path) ? null : path;
    }
}

internal sealed class Api23H2(IVirtualDesktopManagerInternal23H2 m) : ApiBase
{
    public static Api23H2 Connect(IServiceProvider10 shell) =>
        new(ObjectArrays.QueryService<IVirtualDesktopManagerInternal23H2>(shell, ShellGuids.CLSID_VirtualDesktopManagerInternal));

    public override int GetCount() => m.GetCount();
    public override object GetCurrent() => m.GetCurrentDesktop();
    public override void Create() => m.CreateDesktop();

    protected override IObjectArray Desktops()
    {
        m.GetDesktops(out var array);
        return array;
    }

    public override void Switch(object desktop, bool animate)
    {
        var d = (IVirtualDesktop)desktop;
        if (animate)
        {
            m.WaitForAnimationToComplete();
            m.SwitchDesktopWithAnimation(d);

            // The switch animates asynchronously and the new desktop's windows stay cloaked until it
            // ends; wait so the caller can focus one of them.
            m.WaitForAnimationToComplete();
        }
        else
        {
            m.SwitchDesktop(d);
        }
    }
}

internal sealed class Api24H2(IVirtualDesktopManagerInternal24H2 m) : ApiBase
{
    public static Api24H2 Connect(IServiceProvider10 shell) =>
        new(ObjectArrays.QueryService<IVirtualDesktopManagerInternal24H2>(shell, ShellGuids.CLSID_VirtualDesktopManagerInternal));

    public override int GetCount() => m.GetCount();
    public override object GetCurrent() => m.GetCurrentDesktop();
    public override void Create() => m.CreateDesktop();

    protected override IObjectArray Desktops()
    {
        m.GetDesktops(out var array);
        return array;
    }

    public override void Switch(object desktop, bool animate)
    {
        var d = (IVirtualDesktop)desktop;
        if (animate)
        {
            m.WaitForAnimationToComplete();
            m.SwitchDesktopWithAnimation(d);

            // The switch animates asynchronously and the new desktop's windows stay cloaked until it
            // ends; wait so the caller can focus one of them.
            m.WaitForAnimationToComplete();
        }
        else
        {
            m.SwitchDesktop(d);
        }
    }
}
