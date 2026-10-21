# Next Handoff

## 下次继续

- 优先处理本轮未发布的 `0.2.0` 版本识别/受控升级：所有改动在工作区，尚未提交推送，线上没有更新。入口 `ComputersView.vue` / `AgentUpdateDialog.vue`；后台 `TelemetryUpdates.cs` / `AgentUpdateEndpoints.cs`；Windows `UpdateWorker.cs` / `SetupUpdater.cs` / `InstallEngine.cs`；协议在 Contracts `AgentUpdates.cs` / `AgentUpdateFiles.cs`。完整约定见 `docs/agent-updates.md`。
- 本轮仅生产项目编译和前端构建通过，未运行任何测试/浏览器/API/实机功能验证，发布/签名/清单流程也未运行。新增 `AgentUpdateTests.cs`、更新 `SetupCliTests.cs` 的独立升级授权输入与断言，测试项目本轮未单独编译。**遵守用户新 AGENTS：一般实现不授权测试；发布脚本与 Windows workflow 含测试，必须取得相应授权后才可运行，不以旧会话测试授权代替。** 没有为绕过规则改掉原有测试步骤。
- 旧 Agent 不支持自升级，需先发布新版并让用户在本地修复一次，明确选择“允许面板升级”。此后仅更高版本可从面板升级，不能拿首个 0.2.0 同版本构建互相覆盖。发布须同次 EXE + `.release.json`，签名后必须重新计算清单；缺清单时手动下载保留但远程升级不可用。当前 development 编译不能接受远程任务。
- 已实现：任务按电脑保存、会话与包摘要固定、默认关闭的本机授权、密码/CSRF/激活凭据、终端互斥、无 GUI 的 SYSTEM 产品维护、原配置/身份/队列/授权保留、真实新会话/心跳/CPU/内存确认、失败恢复及本地修复回执。进程中断后的恢复依赖采集服务能够再启动；服务也无法启动或断电中断可能仍需本机修复，不宣称完成任意断电回退。先静态复核/获得授权做隔离及实机验收，再选择发布；不要直接操作生产电脑测试。

- 当前应用为 `76c6362`，2026-10-21 11:55（北京时间）已上线单命令下载安装入口及接入页复制按钮；下载 EXE 仍为 `c67e088` / `fbea3549…ac38fc5`（93,950,267 字节），不要把应用和安装包当成同一源码版本。公开分发端点只返回脚本/软件，原受保护下载 API 仍需登录。
- Windows 验证 `39540650446` 对应 `167f9f2`，236 项 .NET、20 项 Python及完整发布通过；与应用 `76c6362` 相比只调整测试/清单，运行代码及文档无差异。原生 5.1 子进程需使用自身 PSModulePath；冷启动进度可能在错误流中使用 CLIXML，测试只接受 progress 记录，错误/警告仍失败。没有真实安装 Windows 服务或开启终端权限。
- 公网脚本、完整包摘要、5 项页面资源摘要与 4 项鉴权检查通过；服务器隔离校验与备份恢复已通过，脚本/摘要和部署记录在 `.local/bootstrap-install-20261021/`。最新一致性备份 `/opt/howie-monitor/backups/monitor-20261021-035141.tar.gz`（SHA-256 `382f96566b9d1aaff5e8057e701933b144ecf1de9223c9fa478c582da525bbeb`）。用户下一步在目标 Windows 的管理员 PowerShell 运行接入页命令，输入绑定码并确认，才算完成目标实机安装验收。以下为前次发布记录。

- 最新 Windows 下载包为 CLI 实现 `c67e088`，2026-10-21 11:11（北京时间）已更新；服务器应用继续是 `f553cf9`，两者来源现在不同，不能再把下载包假设成与应用同一提交。只更新文件，无停服或数据迁移。新 EXE SHA-256 `fbea3549265a716eb1f1a1ca718c7d5a7bc7036f0d9b4c277da9dd453ac38fc5`，93,950,267 字节；旧包在 `staging/deploy-cli-20261021/pre-switch/HowieMonitorSetup.exe`。
- 用户选择交互式 CLI，入口为管理员 PowerShell 的 `Start-Process -FilePath .\HowieMonitorSetup.exe -ArgumentList '--cli' -NoNewWindow -Wait`；原 GUI 保留，`--help` / `--cli --help` 可读帮助。CLI 不自动提权换账号、不要求 Windows 密码、不提供无人值守参数；新终端授权默认关闭，同账号修复保留现有授权，实际安装前必须明确确认。
- `SetupCli.cs` 复用 `InstallEngine` / `SetupClient`，共享 `SetupUiState` / `ClaimAttempt` 文件格式；先保存绑定请求 ID，网络响应丢失后复用请求。重试保留旧电脑接管说明，不能吞掉替换风险；接入未验证不返回成功，待重启标记不删除。`SetupConsole.cs` 处理 WinExe 的父控制台及已有重定向流，PowerShell 必须等待进程以避免同时读取终端输入。
- Windows Actions `39536282588` 通过 226 项 .NET（含 17 项 CLI）、20 项 Python、59 项前端及真实生成包 CLI 帮助入口/嵌入载荷检查。artifact `12317466359`，ZIP SHA-256 `c84c7420a4a0e2b9732c8a955d45535bca6c1bd63eaa328643a535e5c811f8f9`。本轮未执行目标电脑的交互安装或终端命令，原生控制台人机交互/实际服务安装仍待用户电脑验收。记录在 `.local/cli-installer-20261021/` 与 `.local/deploy-cli-20261021/`。
- 用户此前确认的提示是“上一次卸载仍有使用中文件待清理，请关闭向导并重启后重新安装”。这是 `uninstall-pending.json` 拦截，不是单独空主目录；不要让用户删标记绕过，Windows 的旧延迟删除任务可能仍存在。先重启，再重新安装；已经卸载的机器不属于已有安装修复路径。以下为前次完整部署记录。

- 最新应用与 Windows 安装包 `f553cf9` 已于 2026-10-21 00:17（北京时间）部署并推送。当前 `/opt/howie-monitor/current` 为 `/opt/howie-monitor/releases/20261021-f553cf9`，上一版 `20261020-0959d4a`；旧安装包在服务器 `staging/deploy-uninstall-20261021/pre-switch/HowieMonitorSetup.exe`。原容器/用户/镜像/数据目录和 Nginx 未改。当前 2 台电脑及凭据/通知/密钥保留；不要按旧记录恢复成 4 台电脑。
- Windows Actions `39454440301` 通过 209 项 .NET、20 项 Python、59 项前端及实际安装包运行时/嵌入载荷检查，新增 7 个卸载流程/布局用例已在 Windows 运行。产物 ID `12280236526`，ZIP SHA-256 `53f538cb4580c0d32578af88f87778acd090da5142af42cb2f5e80012d39daa4`；受保护下载的新 EXE SHA-256 `60464df0b5c8971045be5c8aa098e500eeaf2caf159188609c59319219b4cce4`，93,932,360 字节，仍未签名。记录在 `.local/deploy-uninstall-20261021/`。
- 新版向导四步与网页卸载引导均已发布；用户需刷新网页、重新下载安装程序并运行安装/修复，才能更新电脑上旧 EXE 的菜单。备用命令仍为 `"%ProgramFiles%\HowieMonitor\HowieMonitorSetup.exe" --uninstall`。用户已确认旧版能够卸载，不要误判成缺注册、改写注册表或替用户执行卸载。
- 备份 `/opt/howie-monitor/backups/monitor-20261020-161146.tar.gz` 已恢复校验，SHA-256 `4ee0f2e49bf76f9441985d7f4633aff5f6aff672edb23430b98b62b93d4274df`，备份停服 20.93 秒、切换 5.3 秒。隔离容器和私有临时副本均已清理，生产备份/配置只保留在服务器。
- 部署前后两台电脑均离线，GAMES 最后采样 2026-10-20 23:41（北京时间），本轮未执行真实 Windows 命令或自动开启终端授权。下一步让目标电脑在线，运行新版安装/修复并显式勾选当前登录管理员账号授权，再按 `docs/terminal-acceptance.md` 验证真实身份与 ConPTY；不能把 Windows 单元测试/协议模拟器成功写成实机终端验收完成。
- 公网 5 个资源哈希、4 项未登录鉴权和安装包摘要一致；本轮旧线上标签 DOM 可读，但刷新/截图超时，未做新版本线上截图，不要拿旧标签缓存当新前端。仅使用内置浏览器，没有启动外部 Chrome。
- 本机后续跨平台编译可复用经微软 SHA-512 校验的 `.local/tooling/dotnet-10.0.113`；该隔离 SDK 的 packs 下映射官方 WindowsDesktop 引用包后锁定还原通过，未改依赖/锁文件。此前构建日志在 `.local/uninstall-wizard-20261020/`。

- 滚动交互应用 `0959d4a` 已于 2026-10-20 19:12（北京时间）部署并推送。`useScrollExperience.ts` 将 GSAP ScrollTrigger、吸顶段落导航、阅读进度和装饰视差统一清理；不要让实时采样行的 DOM 更新反复调用 refresh（会打断原生 smooth scroll）。只有段落身份或布局尺寸变化才重建/刷新，刷新使用 safe 模式；当前位置按当前段落实际边界计算。
- `useHorizontalRail.ts` 复用到电脑卡片与电脑切换栏；原生横向滚动/CSS 吸附，不拦截纵向滚轮。电脑总览默认横向，可切回网格；方向键仅在轨道自身获焦时接管。减少动态效果关闭视差/吸附，按钮/锚点直接定位。保留电脑 ID、指标身份、表单草稿与终端销毁边界。
- 当前前端 59 项测试和生产构建通过；9 个菜单桌面走查、390px 关键交互与减少动态效果通过。本地截图在 `output/scroll-experience-20261020/`，均为合成验证样本。本次线上 5 个资源哈希与发布包一致、4 项鉴权及实时/历史写入通过；内置浏览器生产截图仍超时。后续按用户体验反馈继续，不需要重复部署当前应用。

- 彻底移除电脑及通知下拉框修复 `2c0a840` 已于北京时间 2026-10-20 18:29 部署并推送。入口在电脑总览卡片和历史记录；确认框展示名称/完整 ID，复验面板密码。本次没有删除任何生产电脑；不要使用真实电脑随意做删除验收。
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

- 当前应用 `/opt/howie-monitor/releases/20261020-0959d4a`，上一个为 `20261020-2c0a840`。容器、镜像、数据目录、安装包、Nginx 保持原样；新记录在服务器 `/opt/howie-monitor/staging/deploy-scroll-20261020/` 和本地忽略目录 `.local/deploy-scroll-20261020/`。
- 备份 `/opt/howie-monitor/backups/monitor-20261020-110255.tar.gz` 恢复校验通过，SHA-256 `19de4053db42eb5f3365b97f4349fa9cf98876afd260c7443a8c777179c7457f`，备份停服 20.16 秒、切换 3.36 秒；4 台电脑/凭据/规则/密钥保留。公网 5 个资源、4 项鉴权通过，序号 38368、1.0 秒前采样已写入历史；没有生产删除、测试通知或终端命令。
- 本轮外层备份命令的 300 秒限时不足以完成恢复检查，已复用原归档续验（161.05 秒），清理 `.snapshot-3a45ed5u` 及失败/新恢复副本。下次执行备份校验不要套过短的外层超时；前台仅短暂停服复制，压缩/恢复校验可耗时数分钟，期间原服务正常运行。
- 以下为此前部署的历史记录，当前版本以上方为准。

- 当前 `/opt/howie-monitor/current` 指向 `/opt/howie-monitor/releases/20261020-2c0a840`，上一个应用为 `20261020-05e985a`。容器、镜像、数据目录、安装包及 Nginx 配置保持原样。新记录位于服务器 `/opt/howie-monitor/staging/deploy-removal-20261020/`，本地脚本和非敏感结果在 `.local/deploy-removal-20261020/`。
- 新备份 `/opt/howie-monitor/backups/monitor-20261020-102352.tar.gz` 已完整恢复校验，SHA-256 `2faa578c8d32494a3d77ee8164aeee752dced5b7737f47b4471036a6841e9f8b`；备份停服约 19.88 秒，切换约 2.9 秒。4 台电脑/注册信息/凭据、通知规则和密钥保留；公网 5 个页面资源哈希一致，原 3 个受保护接口和新删除接口未登录均为 401。真实采样序号 37054、距验收约 1.7 秒，已写入历史，待删除队列为空。
- 隔离发布包演练覆盖双电脑同名/同采样身份、规则隔离、删除密码/CSRF、关闭活动终端、旧凭据失效、重启后另一台保留、删除最后一台后再次重启不导回旧凭据且可生成新绑定码；临时无网络容器和数据已清理。内置浏览器线上读取仍超时，未做生产截图复核，不以本地截图冒充线上证据。
- 下面的终端部署是历史记录：`05e985a` 上一个应用为 `20261020-7ab7225`，记录位于 `/opt/howie-monitor/staging/deploy-terminal-20261020/` 与 `.local/deploy-terminal-20261020/`。
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
