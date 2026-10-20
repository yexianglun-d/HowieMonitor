# Activity Log

## 2026-10-20 / 拉取代码、合并并熟悉项目

- 做了什么：fetch 确认远程增加 `a5b7e56`，本地保留 `eeeaaa0`；合并两条分支，解决 5 个前端文件的重叠修改，保留加载骨架屏、并发曲线刷新、Element Plus 控件和 Toast 操作反馈。阅读共享协议、Windows 采集/上传队列、服务端 API/历史/通知、安装及发布流程。
- 修改文件：合并冲突涉及 `App.vue`、`useMonitor.ts`、`EnrollmentView.vue`、`HistoryView.vue`、`NotificationsView.vue`；同步公开源码校验清单，初始化本目录 4 份轻量记录。其余文件来自远程提交。
- 验证方式：按 npm 锁文件安装依赖，使用 Node 24 运行前端构建和测试；运行 Python unittest；Playwright 使用隔离的本地审查数据进行页面检查。
- 验证结果：前端构建通过、19 项前端测试通过、20 项 Python 测试通过；7 个页面导航、Element Plus 表单保存后 Toast、桌面及手机截图检查通过。验证实例通知关闭，未配置 SendKey，无待发通知。
- 没做完/风险：未推送；Windows Forms 测试与真实 Windows 安装/采集/服务恢复未在 macOS 上执行。源码阅读和隔离实例不能证明生产部署已更新。
- 下次继续：先确认 Git 状态和用户的新需求，再从相关模块入口继续；无需重复实现 Toast 或迁移 Element Plus。
