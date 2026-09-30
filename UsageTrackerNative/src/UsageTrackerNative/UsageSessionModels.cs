using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UsageTrackerNative;

public enum SessionSearchMode
{
    Subject,
    Title,
    Process,
    All
}

public sealed class UsageSession : INotifyPropertyChanged
{
    private const string EmptySubjectLabel = "空分类";
    private string _id = string.Empty;
    private string _processName = string.Empty;
    private string _windowTitle = string.Empty;
    private DateTime _startTime;
    private DateTime _endTime;
    private string? _manualSubject;
    private IReadOnlyList<ParallelActivitySnapshot> _parallelActivities = [];

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetField(ref _windowTitle, value);
    }

    public DateTime StartTime
    {
        get => _startTime;
        set
        {
            if (SetField(ref _startTime, value))
            {
                OnPropertyChanged(nameof(StartText));
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public DateTime EndTime
    {
        get => _endTime;
        set
        {
            if (SetField(ref _endTime, value))
            {
                OnPropertyChanged(nameof(EndText));
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public string? ManualSubject
    {
        get => _manualSubject;
        set
        {
            if (SetField(ref _manualSubject, value)) OnPropertyChanged(nameof(SubjectText));
        }
    }

    public IReadOnlyList<ParallelActivitySnapshot> ParallelActivities
    {
        get => _parallelActivities;
        set
        {
            var normalized = value ?? [];
            if (SetField(ref _parallelActivities, normalized))
            {
                OnPropertyChanged(nameof(HasParallelActivities));
                OnPropertyChanged(nameof(ParallelSummaryText));
            }
        }
    }

    public bool HasParallelActivities => ParallelActivities.Count > 0;
    public string ParallelSummaryText => HasParallelActivities
        ? "并行：" + string.Join("、", ParallelActivities.Take(2).Select(x => x.DisplayText)) + (ParallelActivities.Count > 2 ? $" 等 {ParallelActivities.Count} 项" : string.Empty)
        : string.Empty;
    public string SubjectText => ManualSubject ?? EmptySubjectLabel;
    public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : DateTime.Now - StartTime;
    public string StartText => StartTime.ToString("MM-dd HH:mm:ss");
    public string EndText => EndTime > StartTime ? EndTime.ToString("MM-dd HH:mm:ss") : "进行中";
    public string DurationText => DurationFormatter.Format(Duration);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
