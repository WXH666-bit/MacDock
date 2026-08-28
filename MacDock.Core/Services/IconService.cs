using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDock.Core.Interop;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 图标获取服务：将文件（exe / lnk 目标）转为 BitmapSource 并集中缓存。
/// 约定：所有返回的 BitmapSource 均已 Freeze，可在任意线程使用。
/// </summary>
public sealed class IconService
{
    private const long MaximumBitmapAssetBytes = 16 * 1024 * 1024;
    private const int MaximumBitmapSourceDimension = 8192;
    private const long MaximumBitmapSourcePixels = 16_000_000;
    private const int MaximumBitmapPixelSize = 256;

    private static readonly Lazy<IconService> Lazy = new(() => new IconService());
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    public static IconService Instance => Lazy.Value;

    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    /// <summary>提取串行锁：SHGetImageList / IImageList COM 调用并发不安全，多图标并行提取会偶发失败。</summary>
    private static readonly object ExtractLock = new();

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

        BitmapSource icon;
        lock (ExtractLock)
        {
            // 另一个调用可能在等待提取锁时已经填充缓存，避免重复访问 Shell。
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return cached;
            }

            try
            {
                icon = ExtractIcon(path);
            }
            catch (Exception exception)
            {
                // 图标只是装饰信息；损坏或不受支持的文件不能中断启动台或 Dock 加载。
                Logger.Debug(exception, "无法提取图标，使用透明占位图：{0}", path);
                icon = CreatePlaceholderIcon();
            }
        }

        lock (_lock)
        {
            _cache[key] = icon;
        }

        return icon;
    }

    /// <summary>从文件提取图标并转为已冻结的 BitmapSource。</summary>
    private static BitmapSource ExtractIcon(string path)
    {
        // AppX/MSIX 图标通常是 PNG 资源。直接受限解码可保留真实图标，而不是
        // SHGetFileInfo 返回的通用“图片文件”图标。
        if (IsBitmapAssetPath(path))
        {
            return TryLoadBitmapAsset(path, out var bitmapAsset)
                ? bitmapAsset
                : CreatePlaceholderIcon();
        }

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

    private static bool TryLoadBitmapAsset(string path, out BitmapSource bitmapSource)
    {
        bitmapSource = null!;

        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaximumBitmapAssetBytes)
                return false;

            if (!TryReadBitmapDimensions(path, out var width, out var height)
                || width <= 0
                || height <= 0
                || width > MaximumBitmapSourceDimension
                || height > MaximumBitmapSourceDimension
                || (long)width * height > MaximumBitmapSourcePixels)
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height)
                image.DecodePixelWidth = Math.Min(width, MaximumBitmapPixelSize);
            else
                image.DecodePixelHeight = Math.Min(height, MaximumBitmapPixelSize);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            bitmapSource = image;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsBitmapAssetPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadBitmapDimensions(
        string path,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        var frame = decoder.Frames.FirstOrDefault();
        if (frame is null)
            return false;

        width = frame.PixelWidth;
        height = frame.PixelHeight;
        return true;
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

            IImageList? imageList = null;
            try
            {
                imageList = (IImageList)Marshal.GetObjectForIUnknown(pList);
                var getIconResult = imageList.GetIcon(
                    shfi.iIcon,
                    NativeMethods.ILD_TRANSPARENT,
                    out var hIcon);
                if (getIconResult >= 0)
                    return hIcon;

                if (hIcon != IntPtr.Zero)
                    NativeMethods.DestroyIcon(hIcon);
                return IntPtr.Zero;
            }
            finally
            {
                if (imageList is not null && Marshal.IsComObject(imageList))
                    Marshal.ReleaseComObject(imageList);
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
            NativeMethods.SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            // 注意：SHGetFileInfo 的返回值只是成功标志，绝不能当 HICON 使用
            return shfi.hIcon;
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

    /// <summary>
    /// 将 HICON 转为已冻结的 BitmapSource（不销毁源 HICON，供托盘等外部图标使用）。
    /// 可在后台线程调用；返回的位图已 Freeze，跨线程安全。
    /// </summary>
    public static BitmapSource FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
            return CreatePlaceholderIcon();

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return CreatePlaceholderIcon();
        }
    }

    /// <summary>返回一个全透明占位图标：加载完成前不显示灰色方块，图标就位后直接替换。</summary>
    private static BitmapSource CreatePlaceholderIcon()
    {
        const int size = 48;
        var pixels = new byte[size * size * 4]; // 全零 = 全透明

        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        return bmp;
    }
}
