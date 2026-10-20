# Next Handoff

## 下次继续

- 先运行 `git status --short --branch` 与 `git log -5 --oneline`。应用代码 `42b30e6` 已于 2026-10-20 部署至 `https://monitor.howied.me/`，包含此前 Toast、温度展示、多电脑总览与独立通知规则；本地提交尚未推送 GitHub。
- 总览默认路由 `#computers`，各卡片直接进入对应电脑的总览/通知。`/api/notifications/computer?computerId=...` 必须明确电脑归属；全局 `/api/notifications` 只管理共用发送渠道与无归属旧记录。
- `NotificationStore` 一次性把旧规则复制给当时已接入的电脑；迁移标记持久化，新增电脑默认空规则。不能重跑迁移、把旧任务强行猜测归属，或让保存 A 清空 B 的观察状态。
- 用户反馈“温度都没有”：排查时确认旧版页面只按英文原名搜索，中文“温度”返回 0 项；实际 CPU 上报驱动缺失，其他 GPU/SSD 有可用读数。搜索与展示问题已随新版部署，驱动问题仍独立存在。
- 新温度区域与中文搜索已随 `42b30e6` 部署；生产首页和构建资源已核对。内置浏览器线上 DOM/截图连接超时，本次未完成生产页面视觉复核，不要把本地证据当成线上截图。
- CPU 温度仍需在相应 Windows 电脑安装/修复 PawnIO，再重启 `HowieMonitorAgent` 并验证最新上报；生产部署没有修改 Windows 驱动或安装包。

## 当前部署

- 服务器上的 `howie-monitor` Docker 容器配置保持原样；`/opt/howie-monitor/current` 指向 `/opt/howie-monitor/releases/20261020-42b30e6`，数据位于 `/var/lib/howie-monitor`。
- 已验证备份：`/opt/howie-monitor/backups/monitor-20261020-052017.tar.gz`，SHA-256 为 `4c37e74819873edea788ee205d8baf42817d6b17322d5659ab1cc7a4bc8b6ad8`，同目录保留 `.sha256` 和 `.verified.json`。备份停服约 19.43 秒，正式版本切换约 3.4 秒。
- 原版本保留于 `/opt/howie-monitor/releases/20261019-133500/Howie.Monitor.Server-0.1.0-linux-x64-20261019-132130`；部署清单、演练与切换结果位于服务器 `/opt/howie-monitor/staging/deploy-42b30e6-20261020/`。凭据和数据库仅保留在服务器受限目录，项目日志不记录密码。
- 旧通知 JSON 已一次性迁移为 4 个独立规则列表，各保留原来的 1 条规则。回退旧版本前必须评估新旧通知结构；不能直接覆盖旧快照而重放通知或重置已消耗预算，也不能重跑迁移覆盖用户后续编辑。
- 部署后真实在线电脑采样序号持续增长，历史数据继续写入；其他 3 台原有离线电脑的最后采样仍保留。未主动发送真实测试通知。
- 按用户偏好使用内置浏览器；此前本任务启动的 3 个 Playwright Chrome 会话已关闭，后续不要再批量启动外部 Chrome。

## 已知边界

- 这是个人私有监控面板；不假设公共用户注册或多租户需求。
- 电脑各有凭据/实时状态/历史/通知规则/发送记录。SendKey、全局发送开关和每日预算仍共用，页面已经明确标注。
- 历史查询 1 小时/24 小时/7 天，分别使用 2 秒原始数据/1 分钟汇总/5 分钟汇总；保留期约 1 小时/24 小时/7 天。
- 温度摘要按设备分组，优先有效 CPU 主温度、GPU 核心和磁盘综合温度，不用最高的阈值冒充当前读数；更多传感器可展开。
- 原始指标身份不变；缺失温度保持缺失，不显示假零值。

## 不要重复做

- 保留 `useMonitor.ts` 的取消请求、generation 校验和整批曲线更新。
- 操作反馈走 Toast；离线/过期/温度驱动缺失等持续状态保持可见。
- 不提交真实凭据、运行数据库、浏览器证据，或把本地预览当成生产部署。

## 验证入口

- 前端目录：`npm run build`、`npm test`（当前 35 项）。
- 当前 macOS 可通过忽略目录 `.local/multi-computer-tests/Howie.Monitor.Core.Tests.csproj` 运行不依赖 Windows 的服务端子集（70 项）；Windows 仍应运行仓库原测试项目。
- 根目录：`python3 -m unittest discover -s tests -p 'test_*.py'`。
- Windows 构建、测试和发布遵循 `BUILDING.md` 与 `global.json`。
- 本次真实数据浏览器证据位于忽略目录 `output/playwright/temperatures/`；前次同步证据在 `output/playwright/sync-20261020/`。
- 多电脑与规则隔离的浏览器证据位于 `output/playwright/multi-computer/`，使用隔离本地实例和明确标识的合成采样；发送渠道关闭，无真实通知请求。
