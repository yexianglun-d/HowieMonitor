# Current Status

更新日期：2026-10-20

## 项目一句话

Howie Monitor 是个人使用的 Windows 11 多电脑性能监控工具：采集端通过 HTTPS 上报，私有面板查看实时状态、历史记录和可选的 Server酱通知。

## 当前状态

- 本次同步包含远程 `a5b7e56` 的刷新稳定性、骨架屏和 Element Plus 2.14.7 控件迁移，以及本地 `eeeaaa0` 的统一 Toast；采用合并保留两边历史，未推送。
- 技术栈：.NET 10 / SQLite；Vue 3、TypeScript、Vite、Element Plus、GSAP。构建工具版本见 `global.json`、`BUILDING.md` 和 npm 锁文件。
- 没有新增业务功能，也没有修改生产部署；本轮工作是同步、解决重叠改动、熟悉调用链及验证。
- 验证：前端类型检查与构建通过、19 项前端测试通过、20 项 Python 测试通过；隔离本地实例完成 7 个页面导航、保存 Toast 和桌面/手机布局冒烟检查。
- 验证边界：本轮未运行依赖 Windows Forms 的 .NET 全套测试，也未验证真实 Windows 采集、安装、服务恢复或外部通知送达。

## 核心入口

- `src/Howie.Monitor.Contracts/`：指标、采样、采集身份、绑定流程的共享协议。
- `src/Howie.Monitor.Collector/`：Windows PDH、内存/网络/进程和 LibreHardwareMonitor 传感器采集。
- `src/Howie.Monitor.Agent/Workers.cs`、`AgentQueue.cs`：约 2 秒采样；SQLite 有界队列、实时上报、断线补传及逐采样确认。
- `src/Howie.Monitor.Server/Program.cs`：认证、CSRF、采集与面板 API；`Telemetry*.cs` 管理电脑身份、实时状态和分层历史；`Notification*.cs` 与 `AlertRules.cs` 管理通知规则、队列及发送。
- `src/Howie.Monitor.Web/src/App.vue`：登录和 7 个页面；`useMonitor.ts` 负责轮询和切换竞态，`views/monitor/composables/` 负责历史/通知/接入；`useToast.ts` 和 `ToastHost.vue` 负责即时反馈。
- `src/Howie.Monitor.Setup/`：Windows 安装与绑定；`src/Howie.Monitor.Probe/`：独立采样与诊断报告。
- 构建发布见 `BUILDING.md`、`scripts/publish-monitor.ps1`、`.github/workflows/`；`.local/` 与 `output/` 为忽略的本地数据和验证产物。

## 继续前先看

1. `03-next-handoff.md`
2. `01-activity-log.md` 最近记录
3. 本轮需求相关代码；不要仅凭本页推断远程或部署状态。
