using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class PackageAssetResolverTests
{
    [Fact]
    public void ResolveLogoPath_PrefersExactAsset()
    {
        using var directory = new TemporaryDirectory();
        var exact = directory.CreateFile(@"Assets\Logo.png");
        directory.CreateFile(@"Assets\Logo.scale-200.png");

        var resolved = PackageAssetResolver.ResolveLogoPath(
            directory.Path,
            @"Assets\Logo.png");

        Assert.Equal(exact, resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveLogoPath_UsesBestScaleQualifiedAssetWhenExactAssetIsAbsent()
    {
        using var directory = new TemporaryDirectory();
        directory.CreateFile(@"Assets\Logo.scale-100.png");
        var preferred = directory.CreateFile(@"Assets\Logo.scale-200.png");
        directory.CreateFile(@"Assets\Logo.scale-400.png");

        var resolved = PackageAssetResolver.ResolveLogoPath(
            directory.Path,
            @"Assets\Logo.png");

        Assert.Equal(preferred, resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveLogoPath_RejectsPathsOutsidePackageRoot()
    {
        using var directory = new TemporaryDirectory();
        var outside = Path.Combine(
            Path.GetDirectoryName(directory.Path)!,
            $"outside-{Guid.NewGuid():N}.png");

        try
        {
            File.WriteAllBytes(outside, [1]);

            Assert.Null(PackageAssetResolver.ResolveLogoPath(directory.Path, outside));
            Assert.Null(PackageAssetResolver.ResolveLogoPath(
                directory.Path,
                Path.Combine("..", Path.GetFileName(outside))));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void IconService_DecodesPngAssetInsteadOfReturningTheGenericFileIcon()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "wide-logo.png");
        var pixels = new byte[320 * 160 * 4];
        Array.Fill(pixels, (byte)0x7f);
        var source = BitmapSource.Create(
            320,
            160,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            320 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = File.Create(path))
            encoder.Save(stream);

        var icon = new IconService().GetIcon(path);

        Assert.True(icon.IsFrozen);
        Assert.Equal(256, icon.PixelWidth);
        Assert.Equal(128, icon.PixelHeight);
    }

    [Fact]
    public void IconService_RejectsBitmapWithAnExcessiveDecodedDimension()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "overly-tall-logo.png");
        var pixels = new byte[1 * 9000 * 4];
        var source = BitmapSource.Create(
            1,
            9000,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = File.Create(path))
            encoder.Save(stream);

        var icon = new IconService().GetIcon(path);

        Assert.True(icon.IsFrozen);
        Assert.Equal(48, icon.PixelWidth);
        Assert.Equal(48, icon.PixelHeight);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"MacDockTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
