using System.Windows.Media;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 渲染层基类 — 每个 Layer 持有一个 DrawingVisual，
/// Render() 时打开 DrawingContext 绘制，UpdateBrushes() 重新获取主题画刷。
/// </summary>
public abstract class RenderLayer
{
    protected readonly TimeDistributionControl _control;
    protected DrawingVisual _visual;

    protected RenderLayer(TimeDistributionControl control)
    {
        _control = control;
        _visual = new DrawingVisual();
    }

    public DrawingVisual Visual => _visual;

    public abstract void Render();
    public abstract void UpdateBrushes();
}
