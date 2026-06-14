<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=waving&color=gradient&customColorList=6,11,20&height=200&section=header&text=%E6%97%B6%E8%BF%B9&fontSize=60&fontColor=f0f0f0&animation=fadeIn&fontAlignY=35&desc=UsageTracker%20for%20Windows&descSize=18&descAlignY=55" alt="时迹" width="600"/>
</p>

<p align="center">
  <b>让每一秒电脑使用时间，清晰可见</b>
</p>

<p align="center">
  <a href="https://github.com/Junmst/UsageTracker/releases/latest">
    <img src="https://img.shields.io/badge/📥-下载最新版-4ECDC4?style=for-the-badge&logo=github" alt="下载"/>
  </a>
  <a href="https://dotnet.microsoft.com/">
    <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&style=flat-square" alt=".NET 8"/>
  </a>
  <a href="https://github.com/dotnet/wpf">
    <img src="https://img.shields.io/badge/WPF-Native-blue?logo=windows&style=flat-square" alt="WPF"/>
  </a>
  <a href="https://www.sqlite.org/">
    <img src="https://img.shields.io/badge/SQLite-3-003B57?logo=sqlite&style=flat-square" alt="SQLite"/>
  </a>
  <a href="./LICENSE">
    <img src="https://img.shields.io/badge/License-MIT-success?style=flat-square" alt="MIT"/>
  </a>
</p>

---

## ✨ 这是什么？

**时迹** 是一款轻量、静默的 Windows 桌面应用。它运行在系统托盘里，自动追踪你打开的每一个软件、每一个窗口——用了多久、什么时候用、按什么分类。所有数据存在本地，用图表直观呈现，帮你搞清楚自己的时间都去了哪里。

**不需要手动打卡，不需要联网注册，安装即用。**

---

## 🎯 核心功能

<table>
<tr>
<td width="50%">

### ⏱ 自动追踪
- 后台每秒采样活跃窗口
- 自动检测空闲 / 休眠 / 关机
- 精确划分会话时间边界
- 视频播放时不会被误判为空闲

</td>
<td width="50%">

### 📊 时间分布图
- 基于 DrawingVisual 高性能渲染
- 日 / 周 / 月视图自由切换
- 鼠标滚轮缩放、拖拽平移
- 点击条形查看窗口详情

</td>
</tr>
<tr>
<td width="50%">

### 🏷 智能分类
- 三级分类体系：大类 → 父类 → 子类
- 关键词规则自动归类进程
- 支持导入导出分类配置
- 按分类查看使用统计

</td>
<td width="50%">

### 📋 会话明细
- 每条使用记录精确到秒
- 支持多选、框选批量操作
- 右键删除可撤销（最多 10 步）
- 搜索、筛选、分类归档

</td>
</tr>
</table>

---

## 🚀 快速开始

### 直接使用（推荐）

1. 前往 [Releases](https://github.com/Junmst/UsageTracker/releases/latest) 下载最新 zip
2. 解压到任意目录
3. 双击 `时迹.exe` 启动
4. 程序最小化到系统托盘，开始自动记录

> 无需安装 .NET 运行时，发布版已包含完整运行时。

### 从源码构建

<details>
<summary><b>📖 展开构建说明</b></summary>

**环境要求**
- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

**构建**
```bash
cd src/UsageTrackerNative
dotnet build -c Release
```

**发布自包含版本**
```bash
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

</details>

---

## 🏗 技术架构

```
UsageTrackerNative/
├── Shell/                          # 模块化 Shell 框架
│   ├── ShellWindow                 #   主窗口，原生标题栏
│   ├── AppModuleDefinition         #   模块定义
│   └── ModuleRegistry             #   模块注册表
│
├── Modules/                        # 七大功能模块
│   ├── Overview/                   #   总览 — 统计卡片 + 日期导航
│   ├── Sessions/                   #   会话明细 — 列表 / 搜索 / 删除 / 撤销
│   ├── TimeDistribution/          #   时间分布图 — DrawingVisual 渲染引擎
│   ├── Stats/                      #   进程统计 + 分类统计
│   ├── Settings/                   #   设置 — 主题 / 导入导出 / 自启动
│   └── SubjectManagement/          #   分类管理 — 三级层级 + 关键词规则
│
├── TimeDistribution/               # 分布图渲染引擎
│   ├── TimeDistributionControl     #   主控件（世界坐标系 + 视口变换）
│   ├── VisualHost × 3             #   背景层 / 内容层 / 叠加层
│   ├── GridLayer                   #   网格线
│   ├── SessionBarLayer             #   会话条形图
│   ├── DateLabelLayer              #   日期标签列
│   ├── HeaderAxisLayer             #   时间轴
│   └── SelectionLayer              #   选中高亮
│
├── UsageTrackerService.cs          # 核心服务：采样 → 存储 → 导入导出
├── UsageTrackerRepository.cs       # SQLite 数据仓储（WAL 模式）
└── MediaPlaybackMonitor.cs         # 音频会话监控（视频播放空闲保护）
```

### 渲染引擎

时间分布图采用 **DrawingVisual + 视口变换** 架构，不同于 WPF DataGrid 等重量级控件：

- **世界坐标** 以分钟为单位，`Zoom` × `Offset` 控制可见区域
- **鼠标中心缩放** — 滚轮缩放以鼠标位置为锚点，焦点不偏移
- **8 个独立渲染层** 分离绘制职责，交互时仅刷新变化层
- **跨日裁剪** — 会话条按日期边界自动切分，支持多日连续视图
- **平滑滚动** — 惯性滚动 + 平移动画，手感流畅

### 数据层

- **SQLite WAL 模式**，读写不互相阻塞
- **心跳检测** — 启动时校验 `LastCapturedAt`，超过 2 分钟自动收束异常会话
- **跨日查询** — 单次 SQL 查取跨日会话，应用层裁剪到目标日期范围
- **JSON 导入导出** — 支持全量数据迁移，兼容旧版 JSON 格式

---

## 📁 项目结构

```
src/UsageTrackerNative/
├── App.xaml(.cs)              # 应用入口、托盘图标、主题管理
├── V2AppContext.cs            # 全局上下文：服务、选中日期、主题状态
├── Shell/                     # 模块化导航框架
├── Modules/                   # 7 个功能模块（每个模块 = XAML + CodeBehind）
├── TimeDistribution/          # 图表渲染引擎
├── Resources/                 # 多语言资源（zh-CN / en-US）
└── Assets/                    # 图标、字体等
```

---

## 📄 许可证

[MIT License](./LICENSE) · 自由使用，随意修改。

---

<p align="center">
  <sub>Built with ❤️ and WPF · 数据存于本地，隐私由你掌控</sub>
</p>
