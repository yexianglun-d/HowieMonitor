# Current Status

更新日期：2026-10-20

## 项目一句话

Howie Monitor 是个人使用的 Windows 11 多电脑性能监控工具：采集端通过 HTTPS 上报，私有面板查看实时状态、历史记录和可选的 Server酱通知。

## 当前状态

- `main` 已通过 `ebdb039` 合并远程刷新稳定性/Element Plus 控件和本地 Toast，温度展示修复已提交为 `76e8452`。本轮增加电脑总览和按电脑独立的通知规则，未推送或部署。
- 新增“电脑总览”作为默认入口，逐电脑显示状态、CPU/GPU/内存、温度、可用空间与采样时间；链接绑定电脑 ID，手机端保留 ID 以区分同名电脑。
- 通知规则、草稿、观察状态、待发任务和发送记录按电脑隔离；发送渠道、总开关、每日额度仍为全局设置。升级时旧规则只复制到当时已接入的电脑，后来新增电脑默认无规则。
- 总览和硬件信息新增温度区域，按硬件分别展示；缺失时显示真实原因。详情支持中文/英文温度搜索，保留原指标身份和历史查询。
- 已在用户登录的实际面板复现：中文搜索“温度”没有结果，但上报与详情英文行中有 GPU/SSD 温度；CPU 上报 `driverMissing`，PawnIO 安装/访问标志为 false，错误码 2。
- 当前 UI 修复不能恢复缺失的 CPU 驱动；CPU 采集恢复需在对应 Windows 电脑处理，再确认新的上报。
- 技术栈：.NET 10 / SQLite；Vue 3、TypeScript、Vite、Element Plus、GSAP。工具版本见 `global.json`、`BUILDING.md` 和 npm 锁文件。
- 最新验证：前端类型检查/构建、35 项前端测试、70 项服务端跨平台子集测试、20 项 Python 测试通过；隔离实例验证同名电脑、独立读数、规则保存/切换/删除与手机布局。温度改动此前通过真实上报的只读代理验证。
- 验证边界：未安装或更新 Windows 驱动，未执行生产部署；Windows Forms 全套测试与真机安装/服务恢复未在 macOS 上验证。

## 核心入口

- `src/Howie.Monitor.Contracts/`：指标、采样、采集身份和绑定协议。
- `src/Howie.Monitor.Collector/`：Windows PDH、内存/网络/进程和 LibreHardwareMonitor 传感器采集；`SensorCollector.cs` 保留温度缺失原因。
- `src/Howie.Monitor.Agent/Workers.cs`、`AgentQueue.cs`：约 2 秒采样；SQLite 有界队列、实时上报、断线补传及逐采样确认。
- `src/Howie.Monitor.Server/Program.cs`：认证、CSRF 和 API；`Telemetry*.cs` 管理身份/实时/历史；`Notification*.cs` 与 `AlertRules.cs` 管理提醒。
- `src/Howie.Monitor.Web/src/App.vue`：登录和 7 个页面；`useMonitor.ts` 负责轮询和切换竞态，`views/monitor/composables/` 负责历史/通知/接入。
- `src/Howie.Monitor.Web/src/utils/temperatures.ts` 和 `views/monitor/components/TemperaturePanel.vue`：温度分组、中文标签、主读数和缺失状态；SMART 告警/临界阈值不作为当前温度。
- `views/monitor/ComputersView.vue` 展示各电脑独立摘要；`useComputerNotifications.ts` 管理按电脑的规则草稿、取消请求与保存归属；`NotificationStore.cs` 负责一次性迁移与隔离持久化。
- `src/Howie.Monitor.Setup/`：Windows 安装绑定；`src/Howie.Monitor.Probe/`：独立采样与诊断报告。
- 构建发布见 `BUILDING.md`、`scripts/publish-monitor.ps1`、`.github/workflows/`；`.local/` 与 `output/` 是忽略的本地数据和验证产物。

## 继续前先看

1. `03-next-handoff.md`
2. `01-activity-log.md` 最近记录
3. 本轮需求相关代码；不要仅凭本页推断远程或部署状态。
