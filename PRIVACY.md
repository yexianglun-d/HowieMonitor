# Howie Monitor 隐私说明 / Privacy notice

更新日期：2026 年 10 月 17 日。

Howie Monitor 用于经授权监控自己的 Windows 电脑。当前官方安装程序连接 `https://monitor.howied.me/`；该服务由私人面板运营者管理，不是匿名公共服务。使用绑定码前，请确认你认识并信任这个运营者。自行部署时，接收方是你配置的服务器运营者。

## 发送哪些数据

- 绑定请求包括绑定码、请求标识和电脑名称；修复已有安装时还会使用原设备凭据核对身份。
- 注册信息包括电脑名称、设备标识、Windows 版本、CPU/GPU/磁盘型号及容量、磁盘分区和卷、网卡名称与标识，以及传感器/驱动可用性。设备会话和启动标识用于区分采样。
- 后台采集发送 CPU、GPU、内存、磁盘、网络和可读取传感器的性能数据、采样时间、进程名称、进程编号、可读取的启动时间、CPU 和内存占用，以及采集失败原因、心跳和待上传队列状态。
- HTTPS 接收服务器及其反向代理会看到连接 IP，访问日志由服务器运营者管理。发送请求使用设备令牌认证；网页通过密码和会话保护访问。

项目代码不采集屏幕画面、键盘输入、浏览记录、文件内容、进程命令行或通信内容。错误诊断文字可能包含系统返回的名称或错误信息。

## 何时发送与如何停止

安装向导会展示服务器地址并要求绑定和安装确认。安装后台服务后，验证阶段即开始发送真实采样；验证成功后继续后台运行，并配置为 Windows 启动时运行。采样通常每两秒一次。未确认的采样会暂存本地，网络恢复后尝试补传。

不希望发送时，请取消安装；已有安装可通过 Windows 服务管理停止 `HowieMonitorAgent`，或通过 Windows 应用卸载入口移除。卸载时可以选择保留或删除本地配置、令牌和缓存；保留的数据不会自行上传，除非重新启动或安装采集端。停止和卸载不自动删除服务器上已有数据。

## 保存与接收方

网页可查询最近七天的历史。服务器还保留当前注册信息、最新状态、用户会话和可选通知设置。运营者可能保存数据库及密钥的备份；备份有独立保留期限，在线历史过期不等于所有备份立即删除。需要删除云端记录或备份时，请联系实际服务器运营者。

通知默认关闭。运营者启用 ServerChan Turbo 后，告警标题与内容（例如电脑名称、指标值和触发/恢复时间）会发往 ServerChan，由其向已绑定微信发送消息。SendKey 用于认证该服务，不会发给其他接收人。第三方接收和保留行为以 [ServerChan 官方说明](https://sct.ftqq.com/)为准。未启用时不调用该通知服务。

不包含广告或另外的使用行为统计服务。本项目不向开发者另设的数据收集端发送遥测副本。

## 联系与构建

开源项目问题可通过代码仓库的 Issues 联系维护者；不要公开发送任何监控数据或密钥。私有部署的数据问题请联系你的服务器运营者。源码修改、第三方再发布和自建服务器可能有不同的数据处理行为，请核对相应版本。

English summary: the agent sends authorized computer identity, hardware inventory, performance measurements, process names and resource usage, and health/queue information to the configured HTTPS server. The official installer targets the private service named above and requires its operator's pairing code. Installation validation starts real collection. Stop the Windows service or uninstall to stop transmission. Local queued samples may be uploaded after reconnection. The project does not collect screen contents, keystrokes, browser history, file contents, process command lines or communication payloads. Server operators control access, logs, retention and backups. Optional ServerChan notifications share alert content with that provider. See the Chinese sections above for details.
