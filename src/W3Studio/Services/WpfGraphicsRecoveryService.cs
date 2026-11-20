using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace W3Studio.Services;

/// <summary>
/// Prevents WPF + WebView2 surface loss (solid grey/black window behind native WebView2 HWNDs) on hybrid Intel/NVIDIA
/// Optimus GPUs when scrolling or switching between GPU-accelerated apps (Chrome, Discord, Explorer):
/// 1. Keeps WPF in hardware Direct3D mode by default (mixing GDI SoftwareOnly with a DirectComposition WebView2 child HWND
///    causes DWM to drop the parent window surface during scrolling).
/// 2. Adds WS_CLIPCHILDREN | WS_CLIPSIBLINGS to every WPF top-level window HWND so WPF and WebView2 HWNDs never overwrite each other.
/// 3. Disables Chromium's CalculateNativeWinOcclusion and DirectComposition video overlays in the shared CoreWebView2Environment.
/// 4. Forces a Win32 RedrawWindow + InvalidateVisual whenever any W3 Studio window regains focus, restores, or finishes moving.
/// </summary>
public static class WpfGraphicsRecoveryService
{
    private const int GwlStyle = -16;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private const int WmActivate = 0x0006;
    private const int WmActivateApp = 0x001C;
    private const int WmNcActivate = 0x0086;
    private const int WmDisplayChange = 0x007E;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDwmCompositionChanged = 0x031E;

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwInternalPaint = 0x0002;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;
    private const uint RdwFrame = 0x0400;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static readonly object EnvLock = new();
    private static Task<CoreWebView2Environment>? _sharedWebView2EnvTask;
    private static bool _globalHooksInstalled;
    private static readonly ConditionalWeakTable<Window, object> AttachedWindows = new();

    public static void ApplyRenderMode(bool useSafeSoftwareRendering)
    {
        try
        {
            RenderOptions.ProcessRenderMode = useSafeSoftwareRendering
                ? RenderMode.SoftwareOnly
                : RenderMode.Default;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("WpfGraphicsRecovery.ApplyRenderMode", "Failed to switch WPF ProcessRenderMode.", ex);
        }
    }

    public static Task<CoreWebView2Environment> GetSharedWebView2EnvironmentAsync()
    {
        lock (EnvLock)
        {
            if (_sharedWebView2EnvTask == null || _sharedWebView2EnvTask.IsFaulted || _sharedWebView2EnvTask.IsCanceled)
            {
                _sharedWebView2EnvTask = CreateSharedEnvironmentInternalAsync();
            }
            return _sharedWebView2EnvTask;
        }
    }

    private static async Task<CoreWebView2Environment> CreateSharedEnvironmentInternalAsync()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var webViewEnvDir = Path.Combine(appData, "W3Studio", "WebView2Profile");
        Directory.CreateDirectory(webViewEnvDir);

        // Disable Chromium window-occlusion & DirectComposition overlay promotion that cause DWM black/grey parent windows on Optimus GPUs
        var options = new CoreWebView2EnvironmentOptions(
            "--disable-features=CalculateNativeWinOcclusion,RendererCodeIntegrity " +
            "--disable-direct-composition-video-overlays " +
            "--disable-backgrounding-occluded-windows");

        return await CoreWebView2Environment.CreateAsync(null, webViewEnvDir, options);
    }

    public static void InstallGlobalWindowRecovery()
    {
        if (_globalHooksInstalled)
            return;
        _globalHooksInstalled = true;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyWindowLoaded),
            handledEventsToo: true);
    }

    private static void EnsureClipChildrenStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            if (IntPtr.Size == 8)
            {
                long style = GetWindowLongPtr64(hwnd, GwlStyle).ToInt64();
                long desired = style | WsClipChildren | WsClipSiblings;
                if (desired != style)
                    SetWindowLongPtr64(hwnd, GwlStyle, new IntPtr(desired));
            }
            else
            {
                int style = GetWindowLong32(hwnd, GwlStyle);
                int desired = style | WsClipChildren | WsClipSiblings;
                if (desired != style)
                    SetWindowLong32(hwnd, GwlStyle, desired);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("WpfGraphicsRecovery.EnsureClipChildrenStyle", "Could not apply WS_CLIPCHILDREN to window.", ex);
        }
    }

    private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window win)
            return;
        if (AttachedWindows.TryGetValue(win, out _))
            return;
        AttachedWindows.Add(win, new object());

        win.Activated += (_, _) => ScheduleWindowRedraw(win);
        win.StateChanged += (_, _) =>
        {
            if (win.WindowState != WindowState.Minimized)
                ScheduleWindowRedraw(win);
        };

        try
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            EnsureClipChildrenStyle(hwnd);

            var source = PresentationSource.FromVisual(win) as HwndSource;
            source?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if ((msg == WmActivate && wParam != IntPtr.Zero) ||
                    (msg == WmActivateApp && wParam != IntPtr.Zero) ||
                    (msg == WmNcActivate && wParam != IntPtr.Zero) ||
                    msg == WmExitSizeMove ||
                    msg == WmDisplayChange ||
                    msg == WmDwmCompositionChanged)
                {
                    ScheduleWindowRedraw(win);
                }
                return IntPtr.Zero;
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("WpfGraphicsRecovery.AttachHook", "Could not install HwndSource redraw hook.", ex);
        }
    }

    public static void ScheduleWindowRedraw(Window? window)
    {
        if (window == null || !window.IsLoaded)
            return;

        window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            try
            {
                if (!window.IsLoaded || window.WindowState == WindowState.Minimized)
                    return;

                window.InvalidateVisual();
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    EnsureClipChildrenStyle(hwnd);
                    RedrawWindow(
                        hwnd,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        RdwInvalidate | RdwInternalPaint | RdwErase | RdwFrame | RdwAllChildren | RdwUpdateNow);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WpfGraphicsRecovery.Redraw", "Window redraw recovery failed.", ex);
            }
        }));
    }
}
