using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UniversalSpellCheck.UI;

/// <summary>
/// ScrollViewer with browser-like smooth trackpad scrolling (per-frame lerp via
/// <see cref="CompositionTarget.Rendering"/>). Mouse wheels use the default WPF path.
/// </summary>
internal class SmoothScrollViewer : ScrollViewer
{
    private const double LerpFactor = 0.42;
    private const double TargetFrameTime = 1.0 / 144.0;

    private int _lastScrollDelta;
    private int _lastScrollTimestamp;
    private double _currentOffset;
    private double _targetOffset;
    private long _lastRenderTimestamp;
    private bool _isRenderingHooked;
    private bool _isInternalScrollChange;

    public SmoothScrollViewer()
    {
        CanContentScroll = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        PanningMode = PanningMode.VerticalOnly;

        Loaded += (_, _) => _currentOffset = VerticalOffset;
        Unloaded += OnUnloaded;

        DependencyPropertyDescriptor
            .FromProperty(VerticalOffsetProperty, typeof(ScrollViewer))
            .AddValueChanged(this, OnExternalScrollChanged);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DependencyPropertyDescriptor
            .FromProperty(VerticalOffsetProperty, typeof(ScrollViewer))
            .RemoveValueChanged(this, OnExternalScrollChanged);

        StopRendering();
    }

    private void OnExternalScrollChanged(object? sender, EventArgs e)
    {
        if (!_isInternalScrollChange)
            _currentOffset = VerticalOffset;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsTouchpadScroll(e))
        {
            base.OnMouseWheel(e);
            _currentOffset = VerticalOffset;
            return;
        }

        e.Handled = true;
        _targetOffset = Math.Clamp(_currentOffset - e.Delta, 0, ScrollableHeight);
        EnsureRendering();
    }

    private bool IsTouchpadScroll(MouseWheelEventArgs e)
    {
        var wheelLine = Mouse.MouseWheelDeltaForOneLine;
        var isTouchpad = e.Delta % wheelLine != 0
            || (_lastScrollTimestamp > 0
                && e.Timestamp - _lastScrollTimestamp < 100
                && _lastScrollDelta % wheelLine != 0);

        _lastScrollDelta = e.Delta;
        _lastScrollTimestamp = e.Timestamp;
        return isTouchpad;
    }

    private void EnsureRendering()
    {
        if (_isRenderingHooked)
            return;

        _lastRenderTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        _isRenderingHooked = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var deltaTime = (double)(now - _lastRenderTimestamp) / Stopwatch.Frequency;
        _lastRenderTimestamp = now;
        var timeFactor = deltaTime / TargetFrameTime;

        var lerpAmount = 1.0 - Math.Pow(1.0 - LerpFactor, timeFactor);
        _currentOffset += (_targetOffset - _currentOffset) * lerpAmount;

        if (Math.Abs(_targetOffset - _currentOffset) < 0.5)
        {
            _currentOffset = _targetOffset;
            StopRendering();
        }

        InternalScrollToVerticalOffset(_currentOffset);
    }

    private void InternalScrollToVerticalOffset(double offset)
    {
        _isInternalScrollChange = true;
        ScrollToVerticalOffset(offset);
        _isInternalScrollChange = false;
    }

    private void StopRendering()
    {
        if (!_isRenderingHooked)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _isRenderingHooked = false;
    }
}
