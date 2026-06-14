# 时迹 (UsageTracker)

> Windows 桌面使用时长自动统计工具 —— 记录每一刻，让时间可见。

**[📥 下载最新版 (时迹.zip)](https://github.com/Junmst/UsageTracker/releases/latest/download/时迹.zip)**

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/WPF-Windows-blue?logo=windows)](https://github.com/dotnet/wpf)
[![SQLite](https://img.shields.io/badge/SQLite-3-003B57?logo=sqlite)](https://www.sqlite.org/)
[![License](https://img.shields.io/badge/license-MIT-green)](./LICENSE)

---

## 简介

**时迹** 是一款 Windows 桌面应用，静默运行于系统托盘，自动追踪每个进程的使用时长。它记录你打开了什么软件、什么窗口、用了多久，并以图表和统计的形式呈现，帮助你直观了解自己的电脑使用习惯。

### 核心能力

- **自动追踪**：后台每秒采样当前活跃窗口，无需手动操作
- **会话记录**：自动检测空闲、休眠、关机，准确划分会话边界
- **时间分布图**：基于 DrawingVisual 的高性能图表，日/周/月视图自由缩放
- **分类管理**：自定义分类规则，按关键词自动归类进程
- **统计概览**：总使用时长、日均使用、进程排行、分类排行
- **数据导入导出**：JSON 格式，支持跨设备迁移

---

## 快速开始

### 环境要求

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（仅构建需要，发布版自带运行时）

### 构建源码

```bash
cd src/UsageTrackerNative
dotnet build -c Release
```

### 发布（自包含）

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

发布产物为自包含部署，无需额外安装 .NET Runtime。

### 直接使用

下载上方 zip 压缩包，解压后启动 `时迹.exe`，程序将最小化到系统托盘，开始自动记录。

---

## 技术架构

```
时迹 (UsageTrackerNative)
├── Shell/                      # 模块化 Shell 框架
│   ├── ShellWindow             # 主窗口，Windows 原生标题栏
│   ├── AppModuleDefinition     # 模块定义
│   └── ModuleRegistry          # 模块注册表
├── V2AppContext                # 全局上下文（服务、日期、主题）
├── Modules/                    # 功能模块
│   ├── Overview/               # 总览（统计卡片、日期切换）
│   ├── Sessions/               # 会话明细（DataGrid + 搜索/删除/撤销）
│   ├── TimeDistribution/       # 时间分布图（DrawingVisual 渲染）
│   ├── Stats/                  # 进程统计 & 分类统计
│   ├── Settings/               # 设置（主题、导入导出、自启动）
│   └── SubjectManagement/      # 分类管理（大类→父类→子类→关键词）
├── TimeDistribution/           # 图表引擎
│   ├── TimeDistributionControl # 主控件
│   ├── VisualHost              # 3 个 VisualHost 容器
│   ├── GridLayer               # 网格线层
│   ├── SessionBarLayer         # 会话条层
│   ├── DateLabelLayer          # 日期标签层
│   ├── HeaderAxisLayer         # 时间轴层
│   ├── HeaderGridLineLayer     # 表头网格线层
│   └── SelectionLayer          # 选中高亮层
├── UsageTrackerService.cs      # 核心服务（采样、存储、导入导出）
├── UsageTrackerRepository.cs   # SQLite 数据仓储
└── MediaPlaybackMonitor.cs     # 音频会话监控（空闲保护）
```

### 渲染架构

时间分布图表采用 **DrawingVisual 线程渲染**架构，轻于 UIElement 树：

- **3 个 VisualHost**：背景层、内容层、叠加层
- **8 个渲染层**：网格、会话条、表头轴线、日期标签、网格线、选中高亮等
- **视口-世界坐标分离**：`Zoom` × `Offset` 控制视图，世界坐标以分钟为单位
- **鼠标中心缩放**：滚轮缩放以鼠标位置为中心，不偏移视口焦点
- **跨日裁剪**：会话条按日期边界裁剪成片段，支持多日视图

### 数据存储

- **SQLite** 本地数据库（WAL 模式）
- **ActiveSession 表**：记录当前活跃会话，含 `LastCapturedAt` 心跳字段
- **跨机恢复**：启动时检查心跳间隔，超过 2 分钟自动收束异常会话
- **跨日查询**：会话重跨条件查询，自动裁剪跨日片段到目标日期范围

---

## 项目结构

```
src/UsageTrackerNative/
├── App.xaml(.cs)            # 应用入口、托盘、主题
├── Shell/                   # 模块化框架
├── Modules/                 # 7 个功能模块
├── TimeDistribution/        # 图表渲染引擎
├── UsageTrackerService.cs   # 核心服务
├── UsageTrackerRepository.cs # 数据仓储
└── Assets/                  # 图标等资源
```

---

## 模块说明

| 模块 | 说明 |
|------|------|
| **总览** | 日/周/总使用时长统计卡片，日期切换 |
| **会话明细** | DataGrid 列表，支持搜索、框选删除（可撤销）、分类归档 |
| **时间分布** | DrawingVisual 高性能图表，日/周/月视图 |
| **进程统计** | 按进程排行，时间占比可视化 |
| **分类统计** | 按自定义分类排行，三级层级展示 |
| **分类管理** | 大类→父类→子类→关键词规则，支持导入导出 |
| **设置** | 主题色、外观、导入导出、自启动、空闲阈值 |

---

## 许可证

MIT License. 详见 [LICENSE](./LICENSE)。
