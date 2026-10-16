# V2 功能迁移对照

以下为源码实现位置，不代表每项已经过真实游戏验证。Alpha 1.0 不依赖旧版本可执行文件。

| 能力 | C# 实现 |
| --- | --- |
| 任务字段、缺省值、周期日期 | Modules/Launch/Models.cs |
| 原子保存、备份、坏文件保护、历史快照 | Modules/Launch/LaunchStore.cs |
| 自动串行、手动并行、延迟及超时 | Modules/Launch/Scheduler.cs |
| 暂停冻结、已发起运行继续监测 | Scheduler.SetPause/Tick |
| 删除/换目标后独立监测、历史保留 | Scheduler.Changed/Current |
| 清除/重置、完成抑制、跳过、重试 | Scheduler.Clear/Reset/Skip/Retry |
| 进程识别与清理保护、脚本启动 | Modules/Launch/WindowsProcessHost.cs |
| 六个推荐条目、注册表/配置/快捷方式检测；Alpha 1.1 暂时移除终末地 | Modules/Launch/Recommendations.cs |
| 排序、复制、右键、快捷键、拖入文件 | Modules/Launch/LaunchPage.xaml.cs |
| 编辑、选项、取消识别与等待清理 | Modules/Launch/TaskEditor.cs |
| 月份导航、周期日期、历史菜单和键盘 | Modules/Launch/HistoryView.cs |
| 托盘、登录启动、单实例恢复、一次性聚焦 | Services/DesktopHost.cs、App.xaml.cs |
| 最小化/恢复、15 秒退出与保持打开 | MainWindow.xaml.cs |
| 主题、DPI、键盘焦点、列表滚动 | WinUI 控件与应用壳 |

V2 自绘控件与抗锯齿实现由 WinUI 替代。Alpha 1.1 日历采用原生 CalendarView，保留有记录月份跳转入口，原生年月导航允许浏览范围内的空月份。任务操作栏按选择显隐，任务库使用带中文提示的图标。普通按钮恢复标准边界，列表、设置与编辑表单采用分组卡片。主题采用 WinUI 资源，不复制 GDI 配色实现。

已在独立 `.build/ReviewData` 暂停会话中检查窗口、表单、保存、列表及空日历，未执行样例任务、未触及旧数据或登录启动项。真实进程、多任务并行、跨周期、保存失败、识别权限、安装布局、托盘、登录启动和完整键盘流程仍需人工验收。
