using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MacDock.Core.Interop;
using NLog;

namespace MacDock.Core.Services;

/// <summary>外部窗口的一次性内存快照及其物理像素边界。</summary>
public sealed record WindowSnapshot(
    BitmapSource Image,
    int Left,
    int Top,
    int Width,
    int Height);

/// <summary>
/// 使用桌面 DC 的 BitBlt 捕获窗口当前可见区域。该路径不向目标进程发送消息，
/// 不写远程内存，也不会延迟或取消 Windows 自己的最小化流程。
/// </summary>
public static class WindowSnapshotService
{
    private const long MaximumSnapshotPixels = 12L * 1024 * 1024;
    private const int MaximumDimension = 8192;
    private const long MaximumBitmapPixels = 1_600_000;
    private const int MaximumBitmapDimension = 1600;
    private static readonly IntPtr HgdiError = new(-1);
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>DWM 合成关闭时，M4 按设计直接降级为系统原生最小化。</summary>
    public static bool IsAnimationSupported
    {
        get
        {
            try
            {
                return NativeMethods.DwmIsCompositionEnabled(out var enabled) >= 0
                    && enabled;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>捕获窗口当前屏幕像素；失败时返回 null，由系统继续原生最小化。</summary>
    public static WindowSnapshot? TryCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero
            || !IsAnimationSupported
            || !NativeMethods.IsWindow(hwnd)
            || !NativeMethods.IsWindowVisible(hwnd)
            || !TryGetVisibleWindowBounds(hwnd, out var bounds))
        {
            return null;
        }

        var width = bounds.right - bounds.left;
        var height = bounds.bottom - bounds.top;
        if (!IsCaptureSizeAllowed(width, height))
            return null;
        var bitmapSize = GetBitmapSize(width, height);

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousObject = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return null;

            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
                return null;

            bitmap = NativeMethods.CreateCompatibleBitmap(
                screenDc,
                bitmapSize.Width,
                bitmapSize.Height);
            if (bitmap == IntPtr.Zero)
                return null;

            previousObject = NativeMethods.SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero || previousObject == HgdiError)
                return null;

            var requiresScaling = bitmapSize.Width != width || bitmapSize.Height != height;
            if (requiresScaling
                && NativeMethods.SetStretchBltMode(memoryDc, NativeMethods.COLORONCOLOR) == 0)
            {
                return null;
            }

            var copied = !requiresScaling
                ? NativeMethods.BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    bounds.left,
                    bounds.top,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT)
                : NativeMethods.StretchBlt(
                    memoryDc,
                    0,
                    0,
                    bitmapSize.Width,
                    bitmapSize.Height,
                    screenDc,
                    bounds.left,
                    bounds.top,
                    width,
                    height,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            if (!copied)
            {
                return null;
            }

            var image = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();

            return new WindowSnapshot(
                image,
                bounds.left,
                bounds.top,
                width,
                height);
        }
        catch (Exception exception) when (
            exception is ExternalException
                or InvalidOperationException
                or ArgumentException)
        {
            Logger.Debug(exception, "窗口快照捕获失败，已降级为系统最小化");
            return null;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero
                && previousObject != IntPtr.Zero
                && previousObject != HgdiError)
            {
                var restored = NativeMethods.SelectObject(memoryDc, previousObject);
                if (restored == IntPtr.Zero || restored == HgdiError)
                    Logger.Debug("恢复 M4 快照内存 DC 原始对象失败");
            }

            if (bitmap != IntPtr.Zero && !NativeMethods.DeleteObject(bitmap))
                Logger.Debug("释放 M4 快照 HBITMAP 失败");
            if (memoryDc != IntPtr.Zero && !NativeMethods.DeleteDC(memoryDc))
                Logger.Debug("释放 M4 快照内存 DC 失败");
            if (screenDc != IntPtr.Zero
                && NativeMethods.ReleaseDC(IntPtr.Zero, screenDc) == 0)
            {
                Logger.Debug("释放 M4 屏幕 DC 失败");
            }
        }
    }

    private static bool TryGetVisibleWindowBounds(IntPtr hwnd, out RECT bounds)
    {
        try
        {
            if (NativeMethods.DwmGetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out bounds,
                    Marshal.SizeOf<RECT>()) >= 0
                && bounds.right > bounds.left
                && bounds.bottom > bounds.top)
            {
                return true;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return NativeMethods.GetWindowRect(hwnd, out bounds);
    }

    internal static bool IsCaptureSizeAllowed(int width, int height)
        => width >= 32
            && height >= 32
            && width <= MaximumDimension
            && height <= MaximumDimension
            && (long)width * height <= MaximumSnapshotPixels;

    /// <summary>
    /// 动画只需要可辨识的缩略图；限制实际 HBITMAP 尺寸，避免 4K 窗口在飞行的
    /// 每一帧都参与大纹理缩放。窗口物理边界仍由 <see cref="WindowSnapshot"/> 原样保留。
    /// </summary>
    internal static (int Width, int Height) GetBitmapSize(int width, int height)
    {
        if (!IsCaptureSizeAllowed(width, height))
            return (0, 0);

        var scale = Math.Min(
            1.0,
            Math.Min(
                MaximumBitmapDimension / (double)width,
                MaximumBitmapDimension / (double)height));
        var scaledPixels = width * (double)height * scale * scale;
        if (scaledPixels > MaximumBitmapPixels)
        {
            scale *= Math.Sqrt(MaximumBitmapPixels / scaledPixels);
        }

        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }
}
