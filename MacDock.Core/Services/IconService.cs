using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDock.Core.Interop;

namespace MacDock.Core.Services;

/// <summary>
/// 图标获取服务：将文件（exe / lnk 目标）转为 BitmapSource 并集中缓存。
/// 约定：所有返回的 BitmapSource 均已 Freeze，可在任意线程使用。
/// </summary>
public sealed class IconService
{
    private static readonly Lazy<IconService> Lazy = new(() => new IconService());
    public static IconService Instance => Lazy.Value;

    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>获取指定文件的图标（带缓存）。</summary>
    public BitmapSource GetIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return CreatePlaceholderIcon();

        var key = path.ToLowerInvariant();
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var icon = ExtractIcon(path);
        lock (_lock)
        {
            _cache[key] = icon;
        }

        return icon;
    }

    /// <summary>从文件提取图标并转为已冻结的 BitmapSource。</summary>
    private static BitmapSource ExtractIcon(string path)
    {
        // 1. 系统图像列表（SHIL_EXTRALARGE，高清大图标）
        var hIcon = GetExtraLargeIcon(path);
        // 2. 回退：SHGetFileInfo 大图标（32px）
        if (hIcon == IntPtr.Zero)
            hIcon = GetLargeIcon(path);

        if (hIcon != IntPtr.Zero)
        {
            try
            {
                return IconHandleToBitmapSource(hIcon);
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }

        // 3. 最终回退：System.Drawing.ExtractAssociatedIcon
        using var icon = Icon.ExtractAssociatedIcon(path);
        if (icon is not null)
            return IconHandleToBitmapSource(icon.Handle);

        return CreatePlaceholderIcon();
    }

    private static IntPtr GetExtraLargeIcon(string path)
    {
        try
        {
            var shfi = new NativeMethods.SHFILEINFO();
            NativeMethods.SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_LARGEICON);
            if (shfi.iIcon < 0)
                return IntPtr.Zero;

            var iid = NativeMethods.IID_IImageList;
            int hr = NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref iid, out var pList);
            if (hr != 0 || pList == IntPtr.Zero)
                return IntPtr.Zero;

            try
            {
                var imageList = (IImageList)Marshal.GetObjectForIUnknown(pList);
                imageList.GetIcon(shfi.iIcon, NativeMethods.ILD_TRANSPARENT, out var hIcon);
                return hIcon;
            }
            finally
            {
                Marshal.Release(pList);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr GetLargeIcon(string path)
    {
        try
        {
            var shfi = new NativeMethods.SHFILEINFO();
            var result = NativeMethods.SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            return shfi.hIcon != IntPtr.Zero ? shfi.hIcon : result;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>将 HICON 转为已冻结的 BitmapSource。</summary>
    private static BitmapSource IconHandleToBitmapSource(IntPtr hIcon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    /// <summary>占位图标（异步加载完成前显示；已冻结，线程安全）。</summary>
    public static BitmapSource GetPlaceholderIcon() => CreatePlaceholderIcon();

    /// <summary>返回一个简单的占位图标，避免空引用。</summary>
    private static BitmapSource CreatePlaceholderIcon()
    {
        const int size = 48;
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x60;     // B
            pixels[i + 1] = 0x60; // G
            pixels[i + 2] = 0x60; // R
            pixels[i + 3] = 0xFF; // A
        }

        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        return bmp;
    }
}
