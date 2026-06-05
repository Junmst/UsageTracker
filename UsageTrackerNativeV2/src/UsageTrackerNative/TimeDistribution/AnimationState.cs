using System;
using System.Diagnostics;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 动画状态基类 — 由 CompositionTarget.Rendering 驱动。
/// </summary>
public abstract class AnimationState
{
    private readonly Stopwatch _stopwatch = new();

    public bool IsAnimating => _stopwatch.IsRunning;

    public void Start() => _stopwatch.Restart();
    public void Stop() => _stopwatch.Reset();

    protected Stopwatch Stopwatch => _stopwatch;

    public void Tick()
    {
        if (!IsAnimating) return;

        var progress = CalculateProgress();
        ApplyProgress(progress);

        if (progress >= 1.0)
        {
            Stop();
        }
    }

    protected abstract double CalculateProgress();
    protected abstract void ApplyProgress(double progress);
}

/// <summary>
/// 缩放动画 — EaseOutCubic 缓动，同时平滑 Zoom / OffsetX / OffsetY。
/// 直接写字段，绕过属性的 ScheduleRender，由 OnRendering 统一刷新。
/// </summary>
public class ZoomAnimationState : AnimationState
{
    private readonly TimeDistributionControl _control;
    private readonly double _startZoom, _endZoom;
    private readonly double _startOffsetX, _endOffsetX;
    private readonly double _startOffsetY, _endOffsetY;
    private readonly double _durationMs;

    public ZoomAnimationState(TimeDistributionControl control,
                              double startZoom, double endZoom,
                              double startOffsetX, double endOffsetX,
                              double startOffsetY, double endOffsetY,
                              double durationMs = 150)
    {
        _control = control;
        _startZoom = startZoom;
        _endZoom = endZoom;
        _startOffsetX = startOffsetX;
        _endOffsetX = endOffsetX;
        _startOffsetY = startOffsetY;
        _endOffsetY = endOffsetY;
        _durationMs = durationMs;
    }

    protected override double CalculateProgress()
    {
        var elapsed = Stopwatch.Elapsed.TotalMilliseconds;
        var t = Math.Min(elapsed / _durationMs, 1.0);
        return 1 - Math.Pow(1 - t, 3); // EaseOutCubic
    }

    protected override void ApplyProgress(double progress)
    {
        // 直接写字段，不触发 ScheduleRender
        _control._zoom = Math.Clamp(_startZoom + (_endZoom - _startZoom) * progress, TimeDistributionControl.MinZoom, TimeDistributionControl.MaxZoom);
        _control._offsetX = _startOffsetX + (_endOffsetX - _startOffsetX) * progress;
        _control._offsetY = _startOffsetY + (_endOffsetY - _startOffsetY) * progress;
        _control.ClampOffset();
        _control.ScheduleRender();
    }
}

/// <summary>
/// 惯性滚动动画 — 速度指数衰减，每帧乘摩擦系数。
/// </summary>
public class InertiaAnimationState : AnimationState
{
    private readonly TimeDistributionControl _control;
    private Vector _velocity;
    private const double Friction = 0.82;
    private const double MinVelocity = 2.0;

    public InertiaAnimationState(TimeDistributionControl control, Vector initialVelocity)
    {
        _control = control;
        _velocity = initialVelocity;
    }

    protected override double CalculateProgress()
    {
        return Math.Abs(_velocity.X) < MinVelocity && Math.Abs(_velocity.Y) < MinVelocity ? 1.0 : 0.0;
    }

    protected override void ApplyProgress(double progress)
    {
        _velocity *= Friction;

        if (_control.BaseWidth * _control._zoom >= _control.ChartViewportWidth) _control._offsetX += _velocity.X / _control.Zoom;
        _control._offsetY += _velocity.Y;
        _control.ClampOffset();
        _control.ScheduleRender();
    }
}

/// <summary>
/// 平滑滚轮滚动动画 — EaseOutCubic 缓动，将离散滚轮事件转为连续移动。
/// 多次滚轮事件叠加时，从当前位置重新计算目标，实现连续累积效果。
/// </summary>
public class SmoothScrollAnimationState : AnimationState
{
    private readonly TimeDistributionControl _control;
    private double _startOffsetX;
    private double _startOffsetY;
    private double _targetOffsetX;
    private double _targetOffsetY;
    private double _durationMs;

    public SmoothScrollAnimationState(TimeDistributionControl control,
                                      double targetOffsetX, double targetOffsetY,
                                      double durationMs = 120)
    {
        _control = control;
        _startOffsetX = control._offsetX;
        _startOffsetY = control._offsetY;
        _targetOffsetX = targetOffsetX;
        _targetOffsetY = targetOffsetY;
        _durationMs = durationMs;
    }

    /// <summary>
    /// 追加滚轮增量：从当前位置重新计算目标，实现连续累积滚动。
    /// </summary>
    public void AppendDelta(double deltaOffsetX, double deltaOffsetY)
    {
        // 保存当前动画进度对应的实际位置
        if (IsAnimating)
        {
            var progress = CalculateProgressRaw();
            _startOffsetX = _startOffsetX + (_targetOffsetX - _startOffsetX) * progress;
            _startOffsetY = _startOffsetY + (_targetOffsetY - _startOffsetY) * progress;
        }
        _targetOffsetX += deltaOffsetX;
        _targetOffsetY += deltaOffsetY;
        // 重启动画计时
        Stopwatch.Restart();
    }

    private double CalculateProgressRaw()
    {
        var elapsed = Stopwatch.Elapsed.TotalMilliseconds;
        var t = Math.Min(elapsed / _durationMs, 1.0);
        return 1 - Math.Pow(1 - t, 3); // EaseOutCubic
    }

    protected override double CalculateProgress()
    {
        return CalculateProgressRaw();
    }

    protected override void ApplyProgress(double progress)
    {
        _control._offsetX = _startOffsetX + (_targetOffsetX - _startOffsetX) * progress;
        _control._offsetY = _startOffsetY + (_targetOffsetY - _startOffsetY) * progress;
        _control.ClampOffset();
        _control.ScheduleRender();
    }
}
