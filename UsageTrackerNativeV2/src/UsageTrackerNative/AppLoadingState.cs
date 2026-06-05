using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UsageTrackerNative;

/// <summary>
/// 应用数据加载状态（L4 UI 状态管理）
/// </summary>
public enum DataLoadPhase
{
    /// <summary>空闲 / 已完成</summary>
    Idle,
    /// <summary>正在加载</summary>
    Loading,
    /// <summary>部分就绪（核心数据已加载，后台仍在工作）</summary>
    Partial,
    /// <summary>全部就绪</summary>
    Loaded,
    /// <summary>加载失败</summary>
    Error
}

public sealed class AppLoadingState : INotifyPropertyChanged
{
    private DataLoadPhase _phase = DataLoadPhase.Idle;
    private string? _message;
    private double _progress;

    public DataLoadPhase Phase
    {
        get => _phase;
        set => SetField(ref _phase, value);
    }

    public string? Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    /// <summary>
    /// 0-100 加载进度（-1 表示不确定进度）
    /// </summary>
    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public bool IsLoading => Phase is DataLoadPhase.Loading or DataLoadPhase.Partial;
    public bool IsLoaded => Phase == DataLoadPhase.Loaded;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        if (propertyName == nameof(Phase))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoaded)));
        }

        return true;
    }
}
