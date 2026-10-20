# Next Handoff

## 下次继续

- 当前代码已整合远程 `a5b7e56` 和本地 Toast 提交 `eeeaaa0`。开始前运行 `git status --short --branch` 与 `git log -5 --oneline` 确认最新状态；本次没有推送。
- 根据用户下一项具体需求进入相关模块，不自动扩展功能或部署。

## 已知边界

- 这是个人私有监控面板；不假设存在公共用户注册或多租户需求。
- 多电脑各自保有凭据、实时状态和历史；通知设置/请求预算共用，触发与恢复状态按电脑判断。
- 历史查询范围为 1 小时、24 小时、7 天，对应 2 秒原始数据、1 分钟汇总、5 分钟汇总。原始数据保留约 1 小时，1 分钟汇总约 24 小时，5 分钟汇总约 7 天。
- 测试中的审查样本不是真实 Windows 实时采集证据。真实安装、传感器、重启恢复和通知送达仍需要对应环境验证。

## 不要重复做

- 保留 `useMonitor.ts` 的取消请求与 generation 校验，以及整批提交曲线和加载状态。
- 操作成功/失败走 `useToast.ts`，不要重新加入持续占位的保存成功条；通知测试排队不等于已送达。
- 不提交 `.local/`、`output/`、真实凭据或运行数据库。

## 验证入口

- 前端：在 `src/Howie.Monitor.Web/` 运行 `npm ci`、`npm run build`、`npm test`。
- Python：根目录运行 `python3 -m unittest discover -s tests -p 'test_*.py'`。
- Windows 全套构建、测试与发布：遵循 `BUILDING.md`，使用 `global.json` 指定的 SDK。
- 本次截图与浏览器证据位于忽略目录 `output/playwright/sync-20261020/`。
