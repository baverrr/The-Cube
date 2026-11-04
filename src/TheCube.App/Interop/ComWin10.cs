using System.Runtime.InteropServices;

namespace TheCube.App.Interop.Win10;

// Windows 10 21H2/22H2 (builds 19044–19045).

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4")]
internal interface IVirtualDesktop
{
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsViewVisible(nint view);

    Guid GetId();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6")]
internal interface IVirtualDesktopManagerInternal
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
}

internal sealed class Api(IVirtualDesktopManagerInternal manager) : IDesktopApi
{
    public static Api Connect(IServiceProvider10 shell) =>
        new(ObjectArrays.QueryService<IVirtualDesktopManagerInternal>(shell, ShellGuids.CLSID_VirtualDesktopManagerInternal));

    public int GetCount() => manager.GetCount();

    public IReadOnlyList<object> GetDesktops()
    {
        manager.GetDesktops(out var array);
        return ObjectArrays.ToList(array, typeof(IVirtualDesktop).GUID);
    }

    public object GetCurrent() => manager.GetCurrentDesktop();

    public Guid GetId(object desktop) => ((IVirtualDesktop)desktop).GetId();

    public string? GetWallpaper(object desktop) => null;

    // Windows 10 has no animated switch.
    public void Switch(object desktop, bool animate) => manager.SwitchDesktop((IVirtualDesktop)desktop);

    public void Create() => manager.CreateDesktop();
}
