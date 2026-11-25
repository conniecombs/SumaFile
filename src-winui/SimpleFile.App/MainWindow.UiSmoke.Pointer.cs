#if DEBUG
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace SimpleFile.App;

// Real mouse input for the UI smoke. A click, a double-click, a right-click and a drag have no automation pattern
// and cannot be raised from code, so the scenarios that need them move the real pointer and press its buttons with
// SendInput. That takes the mouse from whoever is at the machine, so these steps run only when the smoke is started
// with -PointerInput (SUMAFILE_SMOKE_POINTER=1); without it the scenarios skip them. Excluded from release builds.
public sealed partial class MainWindow
{
    private enum SmokeMouseButton
    {
        Left,
        Right,
        Middle,
        X1,
        X2,
    }

    /// <summary>Whether this run may move the real mouse.</summary>
    private static bool SmokePointerInput =>
        string.Equals(Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_POINTER"), "1", StringComparison.Ordinal);

    /// <summary>
    /// The screen position, in physical pixels, of the point a fraction of the way across and down
    /// <paramref name="element"/>.
    /// </summary>
    private (int X, int Y) SmokeScreenPoint(FrameworkElement element, double acrossFraction = 0.5, double downFraction = 0.5)
    {
        var owner = SmokeOwnerWindow(element) ?? throw new InvalidOperationException("No window hosts the element the pointer is sent to.");
        var inContent = element.TransformToVisual(null)
            .TransformPoint(new Point(element.ActualWidth * acrossFraction, element.ActualHeight * downFraction));
        var scale = element.XamlRoot.RasterizationScale;
        var origin = default(SmokeNativePoint);
        SmokeAssert(ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(owner), ref origin), "ClientToScreen failed for the smoke's window");
        return (origin.X + (int)Math.Round(inContent.X * scale), origin.Y + (int)Math.Round(inContent.Y * scale));
    }

    /// <summary>
    /// Brings the element's window to the front and moves the pointer onto the element. Fails, without pressing
    /// anything, when another window covers that point: a click must never land in another program.
    /// </summary>
    private async Task<(int X, int Y)> SmokePointAtAsync(FrameworkElement element, double acrossFraction = 0.5, double downFraction = 0.5)
    {
        var owner = SmokeOwnerWindow(element) ?? throw new InvalidOperationException("No window hosts the element the pointer is sent to.");
        var top = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        await SmokeRemoteWaitAsync(
            () =>
            {
                if (GetForegroundWindow() != top)
                {
                    SmokeTakeForeground(top);
                }

                return GetForegroundWindow() == top;
            },
            () => "the smoke's window to come to the foreground for mouse input");
        element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await Task.Delay(50);
        var (x, y) = SmokeScreenPoint(element, acrossFraction, downFraction);
        var under = GetAncestor(WindowFromPoint(new SmokeNativePoint { X = x, Y = y }), 2);
        SmokeAssert(under == top, $"Another window covers the point ({x}, {y}) the pointer is sent to");
        SmokeMouseMove(x, y);
        await Task.Delay(60);
        return (x, y);
    }

    /// <summary>
    /// Makes <paramref name="top"/> the foreground window. Windows lets a program take the foreground only from
    /// itself, and the smoke is started by a script while another program is in front; sharing the input state of
    /// the thread that owns the foreground window, for the length of the call, is what lets the request through.
    /// </summary>
    private static void SmokeTakeForeground(nint top)
    {
        var front = GetForegroundWindow();
        var frontThread = front == 0 ? 0u : GetWindowThreadProcessId(front, out _);
        var thisThread = GetCurrentThreadId();
        var attached = frontThread != 0 && frontThread != thisThread && AttachThreadInput(thisThread, frontThread, true);
        try
        {
            BringWindowToTop(top);
            SetForegroundWindow(top);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(thisThread, frontThread, false);
            }
        }
    }

    /// <summary>
    /// Clicks <paramref name="element"/> with the real mouse, in its middle or at the given fractions of its width
    /// and height: <paramref name="count"/> 2 is a double-click.
    /// </summary>
    private async Task SmokeClickAsync(
        FrameworkElement element,
        SmokeMouseButton button = SmokeMouseButton.Left,
        int count = 1,
        double acrossFraction = 0.5,
        double downFraction = 0.5)
    {
        await SmokePointAtAsync(element, acrossFraction, downFraction);
        for (var click = 0; click < count; click++)
        {
            SmokeMouseButtonEvent(button, down: true);
            await Task.Delay(40);
            SmokeMouseButtonEvent(button, down: false);
            await Task.Delay(70);
        }
    }

    /// <summary>Drags from the middle of <paramref name="from"/> to the middle of <paramref name="to"/> with the left button.</summary>
    private Task SmokeDragAsync(FrameworkElement from, FrameworkElement to) => SmokeDragAsync(from, 0.5, 0.5, to, 0.5, 0.5);

    /// <summary>
    /// Drags with the left button between two points given as fractions of their elements' sizes.
    /// <paramref name="beforeRelease"/> runs with the button still down over the target, to look at what the drag
    /// shows there.
    /// </summary>
    private async Task SmokeDragAsync(
        FrameworkElement from,
        double fromAcross,
        double fromDown,
        FrameworkElement to,
        double toAcross,
        double toDown,
        Func<Task>? beforeRelease = null)
    {
        var (x, y) = await SmokePointAtAsync(from, fromAcross, fromDown);
        var (toX, toY) = SmokeScreenPoint(to, toAcross, toDown);
        await SmokeDragBetweenAsync(x, y, toX, toY, beforeRelease);
    }

    /// <summary>Drags <paramref name="element"/> by a distance in view pixels.</summary>
    private async Task SmokeDragByAsync(FrameworkElement element, double across, double down)
    {
        var (x, y) = await SmokePointAtAsync(element);
        var scale = element.XamlRoot.RasterizationScale;
        await SmokeDragBetweenAsync(x, y, x + (int)Math.Round(across * scale), y + (int)Math.Round(down * scale));
    }

    private static async Task SmokeDragBetweenAsync(int x, int y, int toX, int toY, Func<Task>? beforeRelease = null)
    {
        SmokeMouseButtonEvent(SmokeMouseButton.Left, down: true);
        await Task.Delay(150);
        const int steps = 12;
        for (var step = 1; step <= steps; step++)
        {
            SmokeMouseMove(x + (toX - x) * step / steps, y + (toY - y) * step / steps);
            await Task.Delay(40);
        }

        // A drop target learns of the drag from moves over it: two more, then time for its answer.
        SmokeMouseMove(toX + 1, toY);
        await Task.Delay(60);
        SmokeMouseMove(toX, toY);
        await Task.Delay(400);
        try
        {
            if (beforeRelease is not null)
            {
                await beforeRelease();
            }
        }
        finally
        {
            // Whatever the check found, the button does not stay down.
            SmokeMouseButtonEvent(SmokeMouseButton.Left, down: false);
        }

        await Task.Delay(100);
    }

    /// <summary>Where the pointer is, to put it back with <see cref="SmokeMouseMove"/> when a scenario ends.</summary>
    private static (int X, int Y) SmokeMousePosition()
    {
        var point = default(SmokeNativePoint);
        return GetCursorPos(ref point) ? (point.X, point.Y) : (0, 0);
    }

    private static void SmokeMouseMove(int x, int y)
    {
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        // An absolute move is given in 65536ths of the virtual desktop.
        var width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        SmokeSendMouse(
            (int)Math.Round((x - GetSystemMetrics(SM_XVIRTUALSCREEN)) * 65535.0 / (width - 1)),
            (int)Math.Round((y - GetSystemMetrics(SM_YVIRTUALSCREEN)) * 65535.0 / (height - 1)),
            0,
            MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    private static void SmokeMouseButtonEvent(SmokeMouseButton button, bool down)
    {
        var (flag, data) = (button, down) switch
        {
            (SmokeMouseButton.Left, true) => (0x0002u, 0u),
            (SmokeMouseButton.Left, false) => (0x0004u, 0u),
            (SmokeMouseButton.Right, true) => (0x0008u, 0u),
            (SmokeMouseButton.Right, false) => (0x0010u, 0u),
            (SmokeMouseButton.Middle, true) => (0x0020u, 0u),
            (SmokeMouseButton.Middle, false) => (0x0040u, 0u),
            (SmokeMouseButton.X1, true) => (0x0080u, 1u),
            (SmokeMouseButton.X1, false) => (0x0100u, 1u),
            (SmokeMouseButton.X2, true) => (0x0080u, 2u),
            _ => (0x0100u, 2u),
        };
        SmokeSendMouse(0, 0, data, flag);
    }

    private static void SmokeSendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new SmokeInput { Type = 0, Mouse = new SmokeMouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags } };
        var sent = SendInput(1, ref input, Marshal.SizeOf<SmokeInput>());
        SmokeAssert(sent == 1, $"SendInput failed: {Marshal.GetLastWin32Error()}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmokeNativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// INPUT with its MOUSEINPUT member, which is also the union's largest. The member holds a pointer, so it starts
    /// on a pointer boundary: 40 bytes on x64.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SmokeInput
    {
        public uint Type;
        public SmokeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmokeMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref SmokeNativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint thread, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(SmokeNativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(ref SmokeNativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref SmokeInput input, int size);
}
#endif
