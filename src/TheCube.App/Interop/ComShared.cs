using System.Runtime.InteropServices;

// Undocumented Windows shell interfaces for virtual desktops. The interface IDs and method orders
// come from MScholtes/VirtualDesktop (MIT, https://github.com/MScholtes/VirtualDesktop), which tracks
// them across Windows builds. Only the vtable prefix up to the last method we call is declared;
// arguments we never pass (IApplicationView, HSTRING) are typed as nint to avoid marshalling them.

namespace TheCube.App.Interop;

internal static class ShellGuids
{
    public static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    /// <summary>Documented, stable: CLSID_VirtualDesktopManager.</summary>
    public static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out int count);

    void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
}

/// <summary>Documented interface (shobjidl_core.h), stable across builds.</summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManager
{
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsWindowOnCurrentVirtualDesktop(nint topLevelWindow);

    Guid GetWindowDesktopId(nint topLevelWindow);

    void MoveWindowToDesktop(nint topLevelWindow, ref Guid desktopId);
}

/// <summary>The calls The Cube needs, implemented once per interface set.</summary>
internal interface IDesktopApi
{
    int GetCount();

    IReadOnlyList<object> GetDesktops();

    object GetCurrent();

    Guid GetId(object desktop);

    /// <summary>Per-desktop wallpaper, or null where Windows has none (Windows 10).</summary>
    string? GetWallpaper(object desktop);

    void Switch(object desktop, bool animate);

    void Create();
}

internal static class ObjectArrays
{
    public static List<object> ToList(IObjectArray array, Guid iid)
    {
        try
        {
            array.GetCount(out var count);
            var list = new List<object>(count);
            for (var i = 0; i < count; i++)
            {
                array.GetAt(i, ref iid, out var item);
                list.Add(item);
            }

            return list;
        }
        finally
        {
            Marshal.ReleaseComObject(array);
        }
    }

    public static T QueryService<T>(IServiceProvider10 shell, Guid service)
    {
        var iid = typeof(T).GUID;
        return (T)shell.QueryService(ref service, ref iid);
    }
}
