# Next Handoff

## 下次继续

- 当前新增待部署变更：彻底移除电脑及通知下拉框修复。入口在电脑总览卡片和历史记录；确认框展示名称/完整 ID，复验面板密码。生产仍是下方记录的 `05e985a`，不要把本地验证当成已上线。
- 清理调用链：`ComputerRemoval.cs` -> `TelemetryRemoval.cs`（先落标记和撤权）-> `TerminalBroker.RemoveComputer` -> `NotificationStore.RemoveComputer` -> SQLite 全部目标行。`ComputerRemovalWorker` 自动续清失败任务；`WithComputer` 保护迟到的通知写入/观察和终端创建/领取，避免清理后重建。保留空 enrollment_active 行及迁移标记，禁止启动时重新导入已撤销引导凭据。
- 浏览器证据：`output/computer-removal-20261020/`，仅内置浏览器、本地明确标注样本，检查移除确认/取消和通知/历史下拉。没有在浏览器实际移除线上或验证实例电脑；真实 DELETE、CSRF、密码和旧凭据 401 由临时 Kestrel 集成测试验证。
- 验证更新：前端 54 项，跨平台服务端子集 81 项（新增 ComputerRemovalTests.cs），终端含真实 HTTP/WS 27 项，Python 20 项均通过。终端 Sweep 的校验回调在锁外执行，并有跨线程锁回归测试。Windows 原完整 Server.Tests 仍需 Windows 运行；本轮不涉及采集端或安装包修改。
- 管理员终端应用 `05e985a` 已于北京时间 2026-10-20 17:44 部署，新版未签名安装包已更新至受保护下载页。默认关闭，没有自动升级/授权目标电脑。下一步在目标 Windows 11 上重新下载安装包，运行修复/升级并显式勾选本机终端授权，再做真实提权、PowerShell 交互与退出清理验收；不能只凭服务器部署成功声明这些已验证。
- 用户已选择当前已登录的管理员账号，不保存 Windows 密码，注销后不可用。授权绑定安装账号 SID；必须有真实提升令牌，拒绝 SYSTEM/服务身份；切换电脑或离开终端页结束会话，断线不重连、不重放输入。逐项验收步骤见 `docs/terminal-acceptance.md`。
- 关键代码：Contracts `Terminal.cs`；Server `TelemetryTerminal.cs`、`TerminalBroker.cs`、`TerminalEndpoints.cs`；Agent `AdminTerminalIdentity.cs`、`WindowsAdminTerminal.cs`、`TerminalWorker.cs`；Web `TerminalView.vue`、`useTerminal.ts` 与 API。终端视图按电脑 ID 销毁重建，不能仅 reset xterm 后保留异步输出队列。
- 终端发布验证：Windows Actions `39401536776` 通过完整 193 项 .NET、20 项 Python、51 项前端、编译与安装包嵌入载荷/运行时检查。源码已推送，产物来源固定为 `05e985ae16afc1237e5a43c33f541025c3b5696e`；ZIP SHA-256 `63a1e017bf2797f2b59a22577ad3b68cad5ed8f53a08a7f99bec02290a228b80`。真实提权、Ctrl+C、注销、断网与子进程清理仍待 Windows 账号实机验收。
- 内置浏览器 UI 检查仅用本地协议模拟器，未执行真实命令。临时模拟器已停止，截图/日志位于 `output/terminal-20261020/`；没有启动外部 Chrome，也没有访问生产服务器。反向代理需要模板中的 WebSocket Upgrade 路由，隐私说明已涵盖终端输入/输出由运营者服务器转发。
- 推送状态校正：本轮开始时 `main` 与 `origin/main` 同为 `34949f0`，以下旧条目中“尚未推送”是历史记录，不代表当前旧版本未同步。新终端本轮已部署；后续部署状态以上方新记录为准。

- 指标行补丁 `7ab7225` 已完成本地验证并部署：数值和来源同一首行基线，原因展开不改变位置；手机端来源与名称同左边界。电脑总览的显卡/温度行同样使用基线；搜索框改为外层控件布局，内层左右留白一致，焦点反馈保留在外框。不要只检查页面标题就声明内容对齐。
- 补丁证据在忽略目录 `output/metric-row-alignment-20261020/`：20 个逻辑处理器、共 31 项指标、长名称/缺失值/展开原因、桌面和平板/手机布局，均为隔离合成样本。实际通知与生产配置没有被测试修改。
- 本轮菜单视觉对齐与柔和动效 `e9b76d0` 已通过隔离实例验收并部署：统一内容边界、标题、面板内边距、工具栏/操作区；删除卡片上浮/缩放与图表独立入场。页面整体 4px / 220ms，实时刷新不重播，键盘和减少动态效果模式无位移。
- 内置浏览器新建本地标签可正常使用；本轮没有启动外部 Chrome。390 / 1024 / 1280 / 1920px 的 8 个菜单、实际保存 Toast 与帧采样证据保存在忽略目录 `output/alignment-motion-20261020/`，均为明确标识的隔离合成采样。
- GPU 修复 `e7be1af` 已验证并部署：旧版把第一条 AMD 核显 0% 显示为电脑的 GPU 占用，忽略已上报的 RTX 5070 Ti 5%。用户明确选择逐显卡名称、占用与曲线；不要改成只显示独显、最高值或多显卡相加。
- `ComputerSummary.gpus` 是逐显卡列表，旧 `gpuPercent` 仅在单显卡时保留；前后端按硬件 ID 分组，组内优先可用 `GPU Core`，不可用时使用 `D3D 3D` 并明确标为 3D 引擎占用。保留原始指标 key，显存/总线/功耗百分比不能作为核心占用。
- 先运行 `git status --short --branch` 与 `git log -5 --oneline`。应用代码 `7ab7225` 已于 2026-10-20 部署至 `https://monitor.howied.me/`，包含行内对齐补丁、视觉对齐、柔和动效、逐显卡修复及此前功能；此前相关提交已推送至 GitHub，见本页上方的状态校正。
- 总览默认路由 `#computers`，各卡片直接进入对应电脑的总览/通知。`/api/notifications/computer?computerId=...` 必须明确电脑归属；全局 `/api/notifications` 只管理共用发送渠道与无归属旧记录。
- `NotificationStore` 一次性把旧规则复制给当时已接入的电脑；迁移标记持久化，新增电脑默认空规则。不能重跑迁移、把旧任务强行猜测归属，或让保存 A 清空 B 的观察状态。
- 用户反馈“温度都没有”：排查时确认旧版页面只按英文原名搜索，中文“温度”返回 0 项；实际 CPU 上报驱动缺失，其他 GPU/SSD 有可用读数。搜索与展示问题已随新版部署，驱动问题仍独立存在。
- 新温度区域与中文搜索已随 `42b30e6` 部署；生产首页和构建资源已核对。内置浏览器线上 DOM/截图连接超时，本次未完成生产页面视觉复核，不要把本地证据当成线上截图。
- CPU 温度仍需在相应 Windows 电脑安装/修复 PawnIO，再重启 `HowieMonitorAgent` 并验证最新上报；生产部署没有修改 Windows 驱动或安装包。

## 当前部署

- 当前 `/opt/howie-monitor/current` 指向 `/opt/howie-monitor/releases/20261020-05e985a`，上一个应用为 `20261020-7ab7225`。容器、镜像、数据目录保持原样。部署记录位于服务器 `/opt/howie-monitor/staging/deploy-terminal-20261020/`，本地脚本和非敏感验证结果在 `.local/deploy-terminal-20261020/`。
- 新安装包 SHA-256 `38de5397bd7753622718bfdc221845d3a5891dc340596c85be5c9760207d6b8c`，93,930,303 字节，仍未签名；旧安装包保留在服务器本次 staging 的 `pre-switch/HowieMonitorSetup.exe`，原 SHA-256 为 `f196467e416e5e310afc51b4c9fe7736f0ffd3129ef75554593f4e7ca3372a49`。Nginx 原配置保留为 `nginx-before.conf`，只新增了两个终端 socket 路径的 Upgrade 转发。
- 本次部署前备份为 `/opt/howie-monitor/backups/monitor-20261020-093415.tar.gz`，SHA-256 `9face49499f103c311ce5b99441861bc7eeb35b95a5d1a9fb2527d6cd7fdca3a`，已做隔离恢复校验。不要下载其中生产数据库/凭据到本地。
- 发布包在无网络容器中通过终端协议验证；临时容器和数据已清理。线上 5 个公网资源哈希、3 个保护接口及 WS 代理检查通过，真实采样/历史继续写入；通知规则、登录密码哈希、设备凭据、密钥保留。本轮未执行生产终端命令，也未主动发送通知。内置浏览器新线上标签仍超时，生产截图验收未完成。
- 以下是此前部署的历史记录，不能用旧路径推断当前版本。

- 服务器上的 `howie-monitor` Docker 容器配置保持原样；`/opt/howie-monitor/current` 指向 `/opt/howie-monitor/releases/20261020-7ab7225`，数据位于 `/var/lib/howie-monitor`。
- 行内补丁上一个版本为 `/opt/howie-monitor/releases/20261020-e9b76d0`；部署记录在 `/opt/howie-monitor/staging/deploy-metric-rows-20261020/`，切换约 2.1 秒。仅样式变更、无数据迁移；通知规则、凭据、密钥和安装包均保留。公网资源与发布包一致，真实上报和历史正常；此次新内置标签导航线上后也读取超时，隔离截图仍不能当作生产截图。
- 视觉版本的上一个版本为 `/opt/howie-monitor/releases/20261020-e7be1af`，切换耗时约 2.32 秒；发布与验证记录在 `/opt/howie-monitor/staging/deploy-alignment-20261020/`。无数据格式变更，隔离容器和临时数据已清理；原规则、登录/配对凭据、密钥及 Windows 安装包均保留。公网资源字节与构建一致，真实采样和历史写入正常；线上旧标签浏览器连接仍超时，不把本地视觉截图当成线上证据。
- 已验证备份：`/opt/howie-monitor/backups/monitor-20261020-052017.tar.gz`，SHA-256 为 `4c37e74819873edea788ee205d8baf42817d6b17322d5659ab1cc7a4bc8b6ad8`，同目录保留 `.sha256` 和 `.verified.json`。备份停服约 19.43 秒，正式版本切换约 3.4 秒。
- 原版本保留于 `/opt/howie-monitor/releases/20261019-133500/Howie.Monitor.Server-0.1.0-linux-x64-20261019-132130`；部署清单、演练与切换结果位于服务器 `/opt/howie-monitor/staging/deploy-42b30e6-20261020/`。凭据和数据库仅保留在服务器受限目录，项目日志不记录密码。
- GPU 修复的上一个版本为 `/opt/howie-monitor/releases/20261020-42b30e6`，切换耗时约 2.51 秒。发布和验证结果位于 `/opt/howie-monitor/staging/deploy-gpus-20261020/`；无网络演练容器和临时数据已清理。此次没有数据格式迁移，回退到 `42b30e6` 只需切换应用版本，不能覆盖通知或历史数据。
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

- 前端目录：`npm run build`、`npm test`（当前 51 项）。
- 当前 macOS 可通过忽略目录 `.local/multi-computer-tests/Howie.Monitor.Core.Tests.csproj` 运行不依赖 Windows 的服务端子集（73 项）；Windows 仍应运行仓库原测试项目。
- 根目录：`python3 -m unittest discover -s tests -p 'test_*.py'`。
- Windows 构建、测试和发布遵循 `BUILDING.md` 与 `global.json`。
- 本次真实数据浏览器证据位于忽略目录 `output/playwright/temperatures/`；前次同步证据在 `output/playwright/sync-20261020/`。
- 多电脑与规则隔离的浏览器证据位于 `output/playwright/multi-computer/`，使用隔离本地实例和明确标识的合成采样；发送渠道关闭，无真实通知请求。
- GPU 真实指标回放与部署证据在忽略目录 `.local/gpu-verification-20261020/`、`.local/deploy-gpus-20261020/`；只保存 GPU 指标和非敏感验证结果，没有生产数据库或真实密钥。内置浏览器截图连接仍超时，未完成此次线上视觉复核。
