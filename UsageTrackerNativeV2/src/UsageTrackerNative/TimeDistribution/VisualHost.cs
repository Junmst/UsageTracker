using System.Windows;
using System.Windows.Media;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// DrawingVisual 容器 — 将 DrawingVisual 集合接入 WPF 可视树。
/// </summary>
public class VisualHost : FrameworkElement
{
    private readonly VisualCollection _visuals;

    public VisualHost()
    {
        _visuals = new VisualCollection(this);
        ClipToBounds = true;
    }

    public void AddVisual(DrawingVisual visual) => _visuals.Add(visual);
    public void RemoveVisual(DrawingVisual visual) => _visuals.Remove(visual);
    public void ClearVisuals() => _visuals.Clear();

    protected override Visual GetVisualChild(int index) => _visuals[index];
    protected override int VisualChildrenCount => _visuals.Count;

    /// <summary>
    /// 让 VisualHost 整个区域响应命中测试，否则鼠标事件穿透到父级。
    /// </summary>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        return new PointHitTestResult(this, hitTestParameters.HitPoint);
    }
}
