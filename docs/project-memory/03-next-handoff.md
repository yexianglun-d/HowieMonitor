# Next Handoff

## 下次继续

- 先运行 `git status --short --branch` 与 `git log -5 --oneline`。当前代码保留合并提交 `ebdb039` 和温度修复 `76e8452`，新增电脑总览与按电脑独立通知规则；本次未推送或部署。
- 总览默认路由 `#computers`，各卡片直接进入对应电脑的总览/通知。`/api/notifications/computer?computerId=...` 必须明确电脑归属；全局 `/api/notifications` 只管理共用发送渠道与无归属旧记录。
- `NotificationStore` 一次性把旧规则复制给当时已接入的电脑；迁移标记持久化，新增电脑默认空规则。不能重跑迁移、把旧任务强行猜测归属，或让保存 A 清空 B 的观察状态。
- 用户反馈“温度都没有”：已确认两项原因。线上只按英文原名搜索，中文“温度”返回 0 项；实际 CPU 上报驱动缺失，其他 GPU/SSD 有可用读数。
- 新温度区域与中文搜索在本地通过只读代理使用真实线上数据验证；不能据此声称线上页面已经更新。
- CPU 温度仍需在相应 Windows 电脑安装/修复 PawnIO，再重启 `HowieMonitorAgent` 并验证最新上报；用户只授权本轮读取实际面板，未执行驱动安装或生产部署。

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
