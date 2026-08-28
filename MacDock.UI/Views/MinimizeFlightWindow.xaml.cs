using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDock.Animations;
using MacDock.Core.Services;

namespace MacDock.UI.Views;

/// <summary>
/// 一次性、鼠标穿透的最小化快照窗口。只播放内存图像，不拥有或修改目标应用窗口。
/// </summary>
public partial class MinimizeFlightWindow : Window
{
    private readonly Rect _source;
    private readonly Rect _target;
    private readonly TimeSpan _duration;
    private readonly Stopwatch _clock = new();
    private bool _rendering;
    private bool _closed;

    public MinimizeFlightWindow(
        BitmapSource snapshot,
        Rect source,
        Rect target,
        TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Evaluate 同时完成严格边界验证，并给构造首帧使用。
        var first = MinimizeFlightAnimation.Evaluate(source, target, 0);
        _source = source;
        _target = target;
        _duration = duration ?? MinimizeFlightAnimation.DefaultDuration;
        if (_duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        InitializeComponent();
        SnapshotImage.Source = snapshot;
        Apply(first);
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowStyleService.ApplyDockStyles(hwnd, clickThrough: true);
    }

    /// <summary>Dock 退出时立即停止动画并释放快照引用。</summary>
    public void Cancel()
    {
        if (_closed)
            return;

        StopRendering();
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_closed || _rendering)
            return;

        _rendering = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_closed)
            return;

        var progress = _clock.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds;
        Apply(MinimizeFlightAnimation.Evaluate(_source, _target, progress));
        if (progress < 1.0)
            return;

        StopRendering();
        Close();
    }

    private void Apply(MinimizeFlightFrame frame)
    {
        Left = frame.Bounds.Left;
        Top = frame.Bounds.Top;
        Width = Math.Max(1, frame.Bounds.Width);
        Height = Math.Max(1, frame.Bounds.Height);
        Opacity = frame.Opacity;
    }

    private void StopRendering()
    {
        if (!_rendering)
            return;

        _rendering = false;
        _clock.Stop();
        CompositionTarget.Rendering -= OnRendering;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Loaded -= OnLoaded;
        StopRendering();
        SnapshotImage.Source = null;
        base.OnClosed(e);
    }
}
