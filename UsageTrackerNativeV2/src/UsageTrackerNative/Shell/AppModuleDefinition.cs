namespace UsageTrackerNative.Shell;

public sealed class AppModuleDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Group { get; init; }
    public required string IconText { get; init; }
    public int Order { get; init; }
    public required Func<V2AppContext, System.Windows.Controls.UserControl> CreateView { get; init; }
}
