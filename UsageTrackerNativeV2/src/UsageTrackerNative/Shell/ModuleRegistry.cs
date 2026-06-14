using UsageTrackerNative.Modules.Overview;
using UsageTrackerNative.Modules.Placeholders;
using UsageTrackerNative.Modules.Sessions;
using UsageTrackerNative.Modules.Settings;
using UsageTrackerNative.Modules.Stats;
using UsageTrackerNative.Modules.SubjectManagement;
using UsageTrackerNative.Modules.TimeDistribution;

namespace UsageTrackerNative.Shell;

public static class ModuleRegistry
{
    public static IReadOnlyList<AppModuleDefinition> CreateDefaultModules()
    {
        return
        [
            new AppModuleDefinition { Id = "overview", Title = LocalizationService.Instance.Get("Module.Overview"), Group = "WORKSPACE", IconText = "⌁", Order = 10, CreateView = context => new OverviewPage(context) },
            new AppModuleDefinition { Id = "sessions", Title = LocalizationService.Instance.Get("Module.Sessions"), Group = "WORKSPACE", IconText = "☷", Order = 20, CreateView = context => new SessionsPage(context) },
            new AppModuleDefinition { Id = "distribution", Title = LocalizationService.Instance.Get("Module.Distribution"), Group = "WORKSPACE", IconText = "◴", Order = 30, CreateView = context => new TimeDistributionPage(context) },
            new AppModuleDefinition { Id = "process", Title = LocalizationService.Instance.Get("Module.Process"), Group = "WORKSPACE", IconText = "▥", Order = 40, CreateView = context => new StatsPage(context, StatsKind.Process) },
            new AppModuleDefinition { Id = "subject", Title = LocalizationService.Instance.Get("Module.Subject"), Group = "WORKSPACE", IconText = "◆", Order = 50, CreateView = context => new StatsPage(context, StatsKind.Subject) },
            new AppModuleDefinition { Id = "subjectManagement", Title = LocalizationService.Instance.Get("Module.SubjectManagement"), Group = "TOOLS", IconText = "✦", Order = 60, CreateView = context => new SubjectManagementPage(context) },
            new AppModuleDefinition { Id = "settings", Title = LocalizationService.Instance.Get("Module.Settings"), Group = "TOOLS", IconText = "◷", Order = 70, CreateView = context => new SettingsPage(context) }
        ];
    }
}
