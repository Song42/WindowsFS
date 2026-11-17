using System.Runtime.InteropServices;
using WindowsFS.App.Controls;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static Task MaximizedWorkArea()
    {
        // Native message buffers only: no HWND, monitor query, visible window, or display changes.
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowSizing.MinMaxInfo>());
        try
        {
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
            foreach (var origin in new[] { (X: 0, Y: 0), (X: -1920, Y: -1080), (X: 1920, Y: 120) })
            foreach (var taskbar in new[] { "bottom", "top", "left", "right", "hidden" })
            {
                var width = (int)(1920 * scale); var height = (int)(1080 * scale);
                var inset = (int)(48 * scale);
                var monitor = new WindowSizing.PixelRect { Left = origin.X, Top = origin.Y, Right = origin.X + width, Bottom = origin.Y + height };
                var work = monitor;
                switch (taskbar)
                {
                    case "bottom": work.Bottom -= inset; break;
                    case "top": work.Top += inset; break;
                    case "left": work.Left += inset; break;
                    case "right": work.Right -= inset; break;
                }
                var limits = new WindowSizing.MinMaxInfo
                {
                    Reserved = new() { X = 7, Y = 9 },
                    MaxPosition = new() { X = -8, Y = -8 },
                    MaxSize = new() { X = width + 16, Y = height + 16 },
                    MinTrackSize = new() { X = 1100, Y = 760 },
                    MaxTrackSize = new() { X = 8000, Y = 4000 }
                };
                Marshal.StructureToPtr(limits, pointer, false);
                WindowSizing.ApplyWorkArea(pointer, monitor, work);
                var actual = Marshal.PtrToStructure<WindowSizing.MinMaxInfo>(pointer);
                var left = monitor.Left + actual.MaxPosition.X;
                var top = monitor.Top + actual.MaxPosition.Y;
                Equal(work.Left, left); Equal(work.Top, top);
                Equal(work.Right, left + actual.MaxSize.X);
                Equal(work.Bottom, top + actual.MaxSize.Y);
                Equal(limits.Reserved, actual.Reserved);
                Equal(limits.MinTrackSize, actual.MinTrackSize);
                Equal(limits.MaxTrackSize, actual.MaxTrackSize);
            }
            var before = Marshal.PtrToStructure<WindowSizing.MinMaxInfo>(pointer);
            WindowSizing.ApplyWorkArea(pointer, default, default);
            Equal(before, Marshal.PtrToStructure<WindowSizing.MinMaxInfo>(pointer));
            WindowSizing.ApplyWorkArea(0, default, default);
        }
        finally { Marshal.FreeHGlobal(pointer); }
        return Task.CompletedTask;
    }
}
