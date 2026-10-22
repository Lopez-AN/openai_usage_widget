using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OpenAIUsageWidget.Desktop;

/// <summary>
/// Keeps a top-level, non-activating tool window immediately above the desktop icon host.
/// No cross-process parenting, shell injection, global hotkey override or topmost window is used.
/// The shell hierarchy is rediscovered, including the Windows 11 24H2 Progman arrangement.
/// </summary>
public sealed class DesktopAnchor : IDisposable
{
    private readonly Window _window;
    private readonly nint _handle;
    private readonly HwndSource _source;
    private readonly Native.WinEventProc _foregroundCallback;
    private readonly DispatcherTimer _recoveryTimer;
    private readonly nint _hook;
    private bool _positioning;
    private bool _dragging;
    private bool _disposed;
    public nint DesktopHost { get; private set; }

    public DesktopAnchor(Window window)
    {
        _window = window;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(WindowProc);
        var extended = Native.GetWindowLongPtr(_handle, -20).ToInt64();
        // TOOLWINDOW + NOACTIVATE, removing APPWINDOW. Clicks still reach the WPF controls.
        Native.SetWindowLongPtr(_handle, -20, new nint((extended | 0x00000080L | 0x08000000L) & ~0x00040000L));
        _foregroundCallback = (_, _, _, _, _, _, _) => QueueReposition();
        _hook = Native.SetWinEventHook(3, 3, 0, _foregroundCallback, 0, 0, 0);
        _recoveryTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => Reposition(), window.Dispatcher);
        Reposition();
    }

    public void BeginDrag() => _dragging = true;
    public void EndDrag() { _dragging = false; Reposition(); }

    private void QueueReposition()
    {
        if (!_disposed && !_window.Dispatcher.HasShutdownStarted)
        {
            try { _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reposition)); }
            catch (InvalidOperationException) { /* Dispatcher shutdown raced the native event. */ }
        }
    }

    public void Reposition()
    {
        if (_disposed || _positioning || _dragging || !Native.IsWindow(_handle)) return;
        _positioning = true;
        try
        {
            DesktopHost = FindDesktopHost();
            if (Native.IsIconic(_handle) || !Native.IsWindowVisible(_handle))
                Native.ShowWindow(_handle, 4); // SW_SHOWNOACTIVATE; never steal the foreground.

            nint insertAfter = new(1); // HWND_BOTTOM, only a fallback when Explorer is unavailable.
            if (DesktopHost != 0)
            {
                // Insert BEFORE the desktop host (SetWindowPos itself takes an insert-AFTER handle).
                insertAfter = Native.GetWindow(DesktopHost, 3); // GW_HWNDPREV
                if (insertAfter == _handle) return; // Already in exactly the right place.
                // Zero means HWND_TOP, not HWND_TOPMOST. The desktop is then the only window below.
            }
            Native.SetWindowPos(_handle, insertAfter, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }
        finally { _positioning = false; }
    }

    public static nint FindDesktopHost()
    {
        var shell = Native.GetShellWindow();
        if (shell != 0 && Native.FindWindowEx(shell, 0, "SHELLDLL_DefView", null) != 0) return shell;
        nint result = 0;
        Native.EnumWindows((window, _) =>
        {
            var name = new StringBuilder(64);
            Native.GetClassName(window, name, name.Capacity);
            if (name.ToString() is "WorkerW" or "Progman" &&
                Native.FindWindowEx(window, 0, "SHELLDLL_DefView", null) != 0)
            { result = window; return false; }
            return true;
        }, 0);
        return result;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE: do not activate, but do deliver the click.
        { handled = true; return new nint(3); }
        if (message == 0x0046 && !_positioning && !_dragging && lParam != 0) // WM_WINDOWPOSCHANGING
        {
            var position = Marshal.PtrToStructure<Native.WindowPosition>(lParam);
            position.Flags |= 0x0004 | 0x0010; // SWP_NOZORDER | SWP_NOACTIVATE
            Marshal.StructureToPtr(position, lParam, false);
        }
        if (message == 0x0005 && wParam == new nint(1)) QueueReposition(); // SIZE_MINIMIZED
        return 0;
    }

    public object Diagnostics()
    {
        var style = Native.GetWindowLongPtr(_handle, -20).ToInt64();
        var order = new List<nint>();
        Native.EnumWindows((window, _) => { order.Add(window); return true; }, 0);
        return new
        {
            desktopHostFound = DesktopHost != 0,
            visible = Native.IsWindowVisible(_handle),
            topmost = (style & 0x8) != 0,
            toolWindow = (style & 0x80) != 0,
            noActivate = (style & 0x08000000) != 0,
            widgetZIndex = order.IndexOf(_handle),
            desktopZIndex = order.IndexOf(DesktopHost),
            aboveDesktop = DesktopHost != 0 && order.IndexOf(_handle) >= 0 && order.IndexOf(_handle) < order.IndexOf(DesktopHost),
            osBuild = Environment.OSVersion.Version.Build
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recoveryTimer.Stop();
        if (_hook != 0) Native.UnhookWinEvent(_hook);
        _source.RemoveHook(WindowProc);
        GC.KeepAlive(_foregroundCallback);
    }

    private static class Native
    {
        internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
        internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint threadId, uint time);
        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowPosition { public nint Hwnd, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
        [DllImport("user32.dll")] internal static extern nint GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder name, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
        [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    }
}
