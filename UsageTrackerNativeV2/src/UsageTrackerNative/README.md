# 时迹 UsageTrackerNative V2

时迹是一个基于 .NET 8 WPF 的本地桌面时间记录工具，用来记录前台窗口使用情况、查看使用明细，并通过分布图观察时间分配。

当前仓库维护的是 V2 版本源码。

## 最近更新

最近一轮修改主要完成了这些内容：

- 导入 / 导出流程重做，支持 ZIP 备份、导入前预览与多种导入策略
- 新增全程序“仅查看模式”，可把导入数据临时加载到内存中浏览，不写入正式库
- 修复异常长会话问题，优化空闲切断与媒体保护逻辑
- 增强并行活动展示，听歌等后台媒体活动可以在使用明细和分布图详情中看到
- 设置页新增 3 个主题色槽，支持左键应用、右键保存当前颜色
- 修复若干 UI 与稳定性问题，包括深色模式表头、弹窗显示、StaticResource 启动异常、发布残留问题

## 主要功能

- 前台窗口使用记录
- 使用明细查看与筛选
- 时间分布图可视化
- 导入 / 导出与备份恢复
- 仅查看模式浏览导入数据
- 主题颜色自定义

## 目录说明

- `UsageTrackerNative.csproj`：主项目文件
- `Modules/`：页面模块（设置、使用明细、概览等）
- `Shell/`：主窗口与应用壳层
- `TimeDistribution/`：时间分布图相关实现
- `UsageTrackerService.cs`：核心记录与状态逻辑
- `UsageTrackerRepository.cs`：数据访问与持久化

## 构建

```bash
dotnet build UsageTrackerNative.csproj -c Release --no-restore
```

## 发布

默认发布目录：`D:\apps\UsageTrackerNativeV2_publish`

```bash
dotnet publish UsageTrackerNative.csproj -c Release -o "D:\apps\UsageTrackerNativeV2_publish" --no-build
```

如果遇到旧产物残留或发布内容不更新，先执行清理，再重新完整发布。

## 运行

发布后主程序为：

- `D:\apps\UsageTrackerNativeV2_publish\时迹.exe`

## 说明

- 本项目为本地桌面应用，数据以本地存储为主
- 仓库内代码会持续随 V2 功能迭代更新
- README 这里只保留简版说明，细节以源码与后续发行说明为准
