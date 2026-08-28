using System.Diagnostics;
using System.Globalization;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

public class MenuBarViewModelTests
{
    private static readonly DateTime Sample = new(2026, 8, 23, 16, 38, 5);

    [Theory]
    [InlineData("notepad", "", "记事本")]
    [InlineData("notepad", "备份清单.txt", "记事本")]
    [InlineData("unknownapp", "我的窗口标题", "我的窗口标题")]
    [InlineData("unknownapp", "", "unknownapp")]
    [InlineData("dwm", "", "MacDock")]
    public void FormatAppName_Priority_FriendlyOverTitleOverProcess(
        string processName, string title, string expected)
    {
        // 映射表命中时即使有标题也优先友好名；未映射回退标题；再退化进程名；内部进程回兜底名
        Assert.Equal(expected, MenuBarViewModel.FormatAppName(processName, string.IsNullOrEmpty(title) ? null : title));
    }

    [Fact]
    public void FormatClock_ChineseCulture_UsesWeekdayMonthDayTime()
    {
        var text = MenuBarViewModel.FormatClock(Sample, new CultureInfo("zh-CN"));

        // 「周X M月d日 HH:mm」
        Assert.Equal("周日 8月23日 16:38", text);
    }

    [Fact]
    public void FormatClock_EnglishCulture_UsesCultureMonthDayPattern()
    {
        var culture = new CultureInfo("en-US");

        var text = MenuBarViewModel.FormatClock(Sample, culture);

        // 英文区域不应出现硬编码的中文「月/日」
        Assert.DoesNotContain("月", text);
        Assert.DoesNotContain("日", text);
        Assert.StartsWith(Sample.ToString("ddd", culture), text);
        Assert.EndsWith("16:38", text);
    }

    [Fact]
    public void FormatClock_UsesTwentyFourHourClock()
    {
        var evening = new DateTime(2026, 8, 23, 21, 5, 0);

        var text = MenuBarViewModel.FormatClock(evening, new CultureInfo("zh-CN"));

        Assert.EndsWith("21:05", text);
    }

    [Fact]
    public void FormatClock_NullCulture_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => MenuBarViewModel.FormatClock(Sample, null!));
    }

    [Fact]
    public async Task AudioRefresh_ConstructorDoesNotBlockAndAppliesCachedResult()
    {
        var factory = new FakeAudioFactory
        {
            Volume = 0.5f,
            Mute = true,
        };
        using var audio = new AudioService(factory);
        using var brightness = new BrightnessService(new FakeBrightnessProvider());

        var uiThreadId = Thread.CurrentThread.ManagedThreadId;
        var stopwatch = Stopwatch.StartNew();
        var viewModel = new MenuBarViewModel(audio, brightness);
        stopwatch.Stop();

        try
        {
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                $"构造函数同步阻塞了 {stopwatch.Elapsed.TotalMilliseconds:F0}ms");

            await factory.FirstVolumeReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEqual(uiThreadId, factory.VolumeReadThreadId);

            await WaitUntilAsync(
                () => viewModel.VolumePercent == "50%" && viewModel.GetVolumeLevel() == 50);

            Assert.True(viewModel.IsAudioAvailable);
            Assert.True(viewModel.IsMuted);
            Assert.Equal("speaker_0", viewModel.VolumeIconState);
        }
        finally
        {
            await DisposeViewModelAsync(viewModel);
        }
    }

    [Fact]
    public async Task AudioRefresh_IsSingleFlightAndEnsureRunsOffUiThread()
    {
        var factory = new FakeAudioFactory
        {
            BlockFirstVolumeRead = true,
            Volume = 0.25f,
        };
        using var audio = new AudioService(factory);
        using var brightness = new BrightnessService(new FakeBrightnessProvider());
        var uiThreadId = Thread.CurrentThread.ManagedThreadId;
        var viewModel = new MenuBarViewModel(audio, brightness);

        try
        {
            await factory.FirstVolumeReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            viewModel.RequestAudioRefreshForTests(ensureNotifier: true, notifyControls: true);
            viewModel.RequestAudioRefreshForTests();

            Assert.Equal(1, factory.VolumeReadCalls);
            Assert.Equal(0, factory.Notifier.EnsureCalls);

            factory.AllowFirstVolumeRead.TrySetResult(true);
            await WaitUntilAsync(
                () => factory.Notifier.EnsureCalls == 1
                    && factory.VolumeReadCalls >= 2);
            await factory.Notifier.EnsureEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await factory.SecondVolumeReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, factory.VolumeReadCalls);
            Assert.Equal(1, factory.Notifier.EnsureCalls);
            Assert.NotEqual(uiThreadId, factory.Notifier.EnsureThreadId);
            Assert.Equal(1, factory.MaximumConcurrentVolumeReads);

            await WaitUntilAsync(() => viewModel.GetVolumeLevel() == 25);
        }
        finally
        {
            factory.AllowFirstVolumeRead.TrySetResult(true);
            await DisposeViewModelAsync(viewModel);
        }
    }

    [Fact]
    public async Task AudioRefresh_DisposeIsBoundedAndDefersReleaseUntilReadReturns()
    {
        var factory = new FakeAudioFactory
        {
            BlockFirstVolumeRead = true,
        };
        using var audio = new AudioService(factory);
        using var brightness = new BrightnessService(new FakeBrightnessProvider());
        var viewModel = new MenuBarViewModel(audio, brightness);

        try
        {
            await factory.FirstVolumeReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var stopwatch = Stopwatch.StartNew();
            viewModel.Dispose();
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                $"Dispose 同步阻塞了 {stopwatch.Elapsed.TotalMilliseconds:F0}ms");

            await viewModel.BrightnessShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(factory.Disposed);

            factory.AllowFirstVolumeRead.TrySetResult(true);
            await factory.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            factory.AllowFirstVolumeRead.TrySetResult(true);
            await DisposeViewModelAsync(viewModel);
        }
    }

    [Fact]
    public async Task AudioWrite_IsQueuedOffTheUiThreadAndUpdatesTheCacheImmediately()
    {
        var factory = new FakeAudioFactory { Volume = 0.3f };
        using var audio = new AudioService(factory);
        using var brightness = new BrightnessService(new FakeBrightnessProvider());
        var uiThreadId = Environment.CurrentManagedThreadId;
        var viewModel = new MenuBarViewModel(audio, brightness);

        try
        {
            await factory.FirstVolumeReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => viewModel.GetVolumeLevel() == 30);

            viewModel.SetVolumeFromFlyout(70);

            Assert.Equal(70, viewModel.GetVolumeLevel());
            await factory.VolumeWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0.7f, factory.Endpoint.Volume, precision: 3);
            Assert.NotEqual(uiThreadId, factory.VolumeWriteThreadId);
        }
        finally
        {
            await DisposeViewModelAsync(viewModel);
        }
    }

    private static async Task DisposeViewModelAsync(MenuBarViewModel viewModel)
    {
        viewModel.Dispose();
        await viewModel.BrightnessShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(2))
                throw new TimeoutException("等待菜单栏音频缓存应用超时。");

            await Task.Delay(10);
        }
    }

    private sealed class FakeBrightnessProvider : IBrightnessProvider
    {
        public bool IsAvailable() => false;

        public int? GetBrightness() => null;

        public bool SetBrightness(int level) => false;
    }

    private sealed class FakeAudioFactory : IAudioEndpointFactory
    {
        private int _volumeReadCalls;
        private int _activeVolumeReads;
        private int _maximumConcurrentVolumeReads;
        private int _volumeReadThreadId;
        private int _volumeWriteThreadId;

        public FakeAudioFactory()
        {
            Endpoint = new FakeAudioEndpoint(this);
        }

        public FakeAudioEndpoint Endpoint { get; }

        public FakeAudioNotifier Notifier { get; } = new();

        public float Volume
        {
            get => Endpoint.Volume;
            init => Endpoint.Volume = value;
        }

        public bool Mute
        {
            get => Endpoint.Mute;
            init => Endpoint.Mute = value;
        }

        public bool BlockFirstVolumeRead { get; init; }

        public TaskCompletionSource<bool> FirstVolumeReadEntered { get; } =
            NewCompletionSource();

        public TaskCompletionSource<bool> SecondVolumeReadEntered { get; } =
            NewCompletionSource();

        public TaskCompletionSource<bool> AllowFirstVolumeRead { get; } =
            NewCompletionSource();

        public TaskCompletionSource<bool> DisposedSignal { get; } =
            NewCompletionSource();

        public TaskCompletionSource<bool> VolumeWriteEntered { get; } =
            NewCompletionSource();

        public int VolumeReadCalls => Volatile.Read(ref _volumeReadCalls);

        public int VolumeReadThreadId => Volatile.Read(ref _volumeReadThreadId);

        public int VolumeWriteThreadId => Volatile.Read(ref _volumeWriteThreadId);

        public int MaximumConcurrentVolumeReads
            => Volatile.Read(ref _maximumConcurrentVolumeReads);

        public bool Disposed { get; private set; }

        public int EnterVolumeRead()
        {
            var call = Interlocked.Increment(ref _volumeReadCalls);
            Volatile.Write(ref _volumeReadThreadId, Thread.CurrentThread.ManagedThreadId);
            var active = Interlocked.Increment(ref _activeVolumeReads);
            UpdateMaximum(ref _maximumConcurrentVolumeReads, active);

            if (call == 1)
            {
                FirstVolumeReadEntered.TrySetResult(true);
                if (BlockFirstVolumeRead)
                    AllowFirstVolumeRead.Task.GetAwaiter().GetResult();
            }
            else if (call == 2)
            {
                SecondVolumeReadEntered.TrySetResult(true);
            }

            Interlocked.Decrement(ref _activeVolumeReads);
            return call;
        }

        public IAudioEndpoint? GetDefaultRender() => Endpoint;

        public void RecordVolumeWrite()
        {
            Volatile.Write(ref _volumeWriteThreadId, Environment.CurrentManagedThreadId);
            VolumeWriteEntered.TrySetResult(true);
        }

        public IAudioVolumeNotifier CreateNotificationSource() => Notifier;

        public void Dispose()
        {
            Disposed = true;
            DisposedSignal.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewCompletionSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static void UpdateMaximum(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (current >= value)
                    return;

                if (Interlocked.CompareExchange(ref target, value, current) == current)
                    return;
            }
        }
    }

    private sealed class FakeAudioEndpoint : IAudioEndpoint
    {
        private readonly FakeAudioFactory _factory;

        public FakeAudioEndpoint(FakeAudioFactory factory)
        {
            _factory = factory;
        }

        public float Volume { get; set; } = 0.5f;

        public bool Mute { get; set; }

        public float? GetVolume()
        {
            _factory.EnterVolumeRead();
            return Volume;
        }

        public bool SetVolume(float level)
        {
            Volume = level;
            _factory.RecordVolumeWrite();
            return true;
        }

        public bool? GetMute() => Mute;

        public bool SetMute(bool mute)
        {
            Mute = mute;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAudioNotifier : IAudioVolumeNotifier
    {
        private int _ensureCalls;
        private int _ensureThreadId;

        public TaskCompletionSource<bool> EnsureEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EnsureCalls => Volatile.Read(ref _ensureCalls);

        public int EnsureThreadId => Volatile.Read(ref _ensureThreadId);

        public string? BoundDeviceId => "fake-device";

        public event Action? VolumeChanged;

        public bool TryRegister() => true;

        public bool EnsureBoundToCurrentDefault()
        {
            Interlocked.Increment(ref _ensureCalls);
            Volatile.Write(ref _ensureThreadId, Thread.CurrentThread.ManagedThreadId);
            EnsureEntered.TrySetResult(true);
            return true;
        }

        public void Raise() => VolumeChanged?.Invoke();

        public void Dispose()
        {
        }
    }
}
