using System.Runtime.InteropServices;

namespace WindowsFS.App.Controls;

/// <summary>Keeps custom-chrome maximized content inside the current monitor's work area.</summary>
internal static class WindowSizing
{
    internal const int GetMinMaxInfoMessage = 0x0024;

    internal static void UpdateMaximizedBounds(nint window, nint minMaxInfo)
    {
        var monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;
        ApplyWorkArea(minMaxInfo, info.Monitor, info.Work);
    }

    internal static void ApplyWorkArea(nint pointer, PixelRect monitor, PixelRect work)
    {
        if (pointer == 0 || work.Right <= work.Left || work.Bottom <= work.Top) return;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(pointer);
        // These Win32 values are physical pixels, including on mixed-DPI/negative-origin monitors.
        // WindowChrome extends client content into the frame, so no invisible resize frame is added.
        limits.MaxPosition = new PixelPoint { X = work.Left - monitor.Left, Y = work.Top - monitor.Top };
        limits.MaxSize = new PixelPoint { X = work.Right - work.Left, Y = work.Bottom - work.Top };
        Marshal.StructureToPtr(limits, pointer, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelPoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public PixelPoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public PixelRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
