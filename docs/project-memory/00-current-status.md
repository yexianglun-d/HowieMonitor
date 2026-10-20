# Current Status

更新日期：2026-10-20

## 项目一句话

Howie Monitor 是个人使用的 Windows 11 多电脑性能监控工具：采集端通过 HTTPS 上报，私有面板查看实时状态、历史记录和可选的 Server酱通知。

## 当前状态

- `main` 已通过 `ebdb039` 合并远程刷新稳定性/Element Plus 控件和本地 Toast，温度展示修复为 `76e8452`，多电脑总览与独立通知规则为 `42b30e6`，逐显卡占用修复为 `e7be1af`。2026-10-20 当前线上应用版本为 `e7be1af`，入口 `https://monitor.howied.me/`；本地提交尚未推送 GitHub。
- 新增“电脑总览”作为默认入口，逐电脑显示状态、CPU/GPU/内存、温度、可用空间与采样时间；链接绑定电脑 ID，手机端保留 ID 以区分同名电脑。
- GPU 误显示 0% 已定位：真实上报同时有 AMD 核显 0% 和 RTX 5070 Ti 5%，旧摘要只取第一条。按用户选择改为逐显卡名称、占用、独立曲线，电脑总览也使用逐显卡列表；修复已验证并部署。
- 通知规则、草稿、观察状态、待发任务和发送记录按电脑隔离；发送渠道、总开关、每日额度仍为全局设置。升级时旧规则只复制到当时已接入的电脑，后来新增电脑默认无规则。
- 总览和硬件信息新增温度区域，按硬件分别展示；缺失时显示真实原因。详情支持中文/英文温度搜索，保留原指标身份和历史查询。
- 已在用户登录的实际面板复现：中文搜索“温度”没有结果，但上报与详情英文行中有 GPU/SSD 温度；CPU 上报 `driverMissing`，PawnIO 安装/访问标志为 false，错误码 2。
- 当前 UI 修复不能恢复缺失的 CPU 驱动；CPU 采集恢复需在对应 Windows 电脑处理，再确认新的上报。
- 技术栈：.NET 10 / SQLite；Vue 3、TypeScript、Vite、Element Plus、GSAP。工具版本见 `global.json`、`BUILDING.md` 和 npm 锁文件。
- 最新验证：前端类型检查/构建、41 项前端测试、73 项服务端跨平台子集测试、20 项 Python 测试通过。真实 GPU 指标本地回放确认前后端逐显卡取值一致，两张显卡的历史独立；此前隔离实例验证过多电脑规则与手机布局。
- 生产部署：先完成停服一致性备份和恢复校验，再用无网络且关闭通知的副本演练迁移；原有 1 条规则分别迁移到 4 台已接入电脑。切换约 3.4 秒，真实上报和历史写入继续，公网页面及 JS/CSS 与发布包逐字节一致；受保护接口未登录返回 401。登录密码、配对凭据、加密密钥和原安装包均保留。
- GPU 修复部署：同一 Linux 发布包在无网络容器中经真实 GPU 指标回放验证，API 返回 2 张显卡与独立历史；正式切换约 2.51 秒。公网资源与 `e7be1af` 发布包一致，线上真实采样继续，最后核对核显 0%、独显 4%。未变更数据结构、通知规则或 Windows 采集端。
- 验证边界：内置浏览器线上 DOM/截图连接超时，本次部署通过服务器状态、真实上报和公网资源校验验收；没有以本地截图冒充线上视觉验证。未安装或更新 Windows 驱动；Windows Forms 全套测试与真机安装/服务恢复未在 macOS 上验证。

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
