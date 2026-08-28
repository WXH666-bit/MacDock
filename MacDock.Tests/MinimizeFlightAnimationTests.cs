using System.Windows;
using MacDock.Animations;
using Xunit;

namespace MacDock.Tests;

public sealed class MinimizeFlightAnimationTests
{
    private static readonly Rect Source = new(120, 80, 1280, 720);
    private static readonly Rect Target = new(920, 980, 56, 56);

    [Fact]
    public void Evaluate_EndpointsMatchSourceAndTargetExactly()
    {
        var first = MinimizeFlightAnimation.Evaluate(Source, Target, 0);
        var last = MinimizeFlightAnimation.Evaluate(Source, Target, 1);

        Assert.Equal(Source, first.Bounds);
        Assert.Equal(1, first.Opacity);
        Assert.Equal(Target, last.Bounds);
        Assert.Equal(0, last.Opacity);
    }

    [Fact]
    public void Evaluate_MidpointShrinksAndStaysFinite()
    {
        var frame = MinimizeFlightAnimation.Evaluate(Source, Target, 0.5);

        Assert.InRange(frame.Bounds.Width, Target.Width, Source.Width);
        Assert.InRange(frame.Bounds.Height, Target.Height, Source.Height);
        Assert.InRange(frame.Opacity, 0, 1);
        Assert.True(double.IsFinite(frame.Bounds.Left));
        Assert.True(double.IsFinite(frame.Bounds.Top));
    }

    [Theory]
    [InlineData(-10, true)]
    [InlineData(10, false)]
    public void Evaluate_ClampsProgress(double progress, bool expectedSource)
    {
        var frame = MinimizeFlightAnimation.Evaluate(Source, Target, progress);

        Assert.Equal(expectedSource ? Source : Target, frame.Bounds);
    }

    [Fact]
    public void Evaluate_RejectsInvalidBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MinimizeFlightAnimation.Evaluate(Rect.Empty, Target, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MinimizeFlightAnimation.Evaluate(Source, new Rect(0, 0, 0, 10), 0.5));
    }
}
