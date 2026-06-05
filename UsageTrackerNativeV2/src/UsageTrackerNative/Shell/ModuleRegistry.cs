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
            new AppModuleDefinition { Id = "overview", Title = "总览", Group = "WORKSPACE", IconText = "⌁", Order = 10, CreateView = context => new OverviewPage(context) },
            new AppModuleDefinition { Id = "sessions", Title = "使用明细", Group = "WORKSPACE", IconText = "☷", Order = 20, CreateView = context => new SessionsPage(context) },
            new AppModuleDefinition { Id = "distribution", Title = "时长分布", Group = "WORKSPACE", IconText = "◴", Order = 30, CreateView = context => new TimeDistributionPage(context) },
            new AppModuleDefinition { Id = "process", Title = "进程统计", Group = "WORKSPACE", IconText = "▥", Order = 40, CreateView = context => new StatsPage(context, StatsKind.Process) },
            new AppModuleDefinition { Id = "subject", Title = "分类统计", Group = "WORKSPACE", IconText = "◆", Order = 50, CreateView = context => new StatsPage(context, StatsKind.Subject) },
            new AppModuleDefinition { Id = "subjectManagement", Title = "分类管理", Group = "TOOLS", IconText = "✦", Order = 60, CreateView = context => new SubjectManagementPage(context) },
            new AppModuleDefinition { Id = "settings", Title = "设置", Group = "TOOLS", IconText = "◷", Order = 70, CreateView = context => new SettingsPage(context) }
        ];
    }
}
