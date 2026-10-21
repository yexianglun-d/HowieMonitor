# Howie Monitor 隐私说明 / Privacy notice

更新日期：2026 年 10 月 21 日。

Howie Monitor 用于经授权监控自己的 Windows 电脑。当前官方安装程序连接 `https://monitor.howied.me/`；该服务由私人面板运营者管理，不是匿名公共服务。使用绑定码前，请确认你认识并信任这个运营者。自行部署时，接收方是你配置的服务器运营者。

## 发送哪些数据

- 绑定请求包括绑定码、请求标识和电脑名称；修复已有安装时还会使用原设备凭据核对身份。
- 注册信息包括电脑名称、设备标识、Windows 版本、CPU/GPU/磁盘型号及容量、磁盘分区和卷、网卡名称与标识，以及传感器/驱动可用性。还包括采集端版本、源码构建标识与面板升级授权状态。设备会话和启动标识用于区分采样。
- 后台采集发送 CPU、GPU、内存、磁盘、网络和可读取传感器的性能数据、采样时间、进程名称、进程编号、可读取的启动时间、CPU 和内存占用，以及采集失败原因、心跳和待上传队列状态。
- HTTPS 接收服务器及其反向代理会看到连接 IP，访问日志由服务器运营者管理。发送请求使用设备令牌认证；网页通过密码和会话保护访问。

后台性能采集不读取屏幕画面、键盘输入、浏览记录、文件内容、进程命令行或通信内容。错误诊断文字可能包含系统返回的名称或错误信息。主动开启远程终端后的数据范围见下文。

## 何时发送与如何停止

安装向导会展示服务器地址并要求绑定和安装确认。安装后台服务后，验证阶段即开始发送真实采样；验证成功后继续后台运行，并配置为 Windows 启动时运行。采样通常每两秒一次。未确认的采样会暂存本地，网络恢复后尝试补传。

不希望发送时，请取消安装；已有安装可通过 Windows 服务管理停止 `HowieMonitorAgent`，或通过 Windows 应用卸载入口移除。卸载时可以选择保留或删除本地配置、令牌和缓存；保留的数据不会自行上传，除非重新启动或安装采集端。停止和卸载不自动删除服务器上已有数据。

## 保存与接收方

网页可查询最近七天的历史。服务器还保留当前注册信息、最新状态、用户会话和可选通知设置。面板所有者可以从电脑总览或历史记录移除指定电脑：再次验证面板密码后，删除该电脑的在线注册信息、实时与历史数据、通知规则、观察状态及发送记录，撤销接入凭据并结束终端会话。其他电脑及共用发送渠道和已消耗额度不受影响。重新接入需要新绑定码。

移除不会卸载目标电脑的采集端，也不会删除电脑本地配置、缓存、既有备份、服务器运行日志或第三方已经接收的通知。已经交付给发送服务的请求无法撤回。运营者可能保存数据库及密钥的备份；备份有独立保留期限，在线删除或历史过期不等于所有备份立即删除。需要清理备份和日志时，请联系实际服务器运营者。

通知默认关闭。运营者启用 ServerChan Turbo 后，告警标题与内容（例如电脑名称、指标值和触发/恢复时间）会发往 ServerChan，由其向已绑定微信发送消息。SendKey 用于认证该服务，不会发给其他接收人。第三方接收和保留行为以 [ServerChan 官方说明](https://sct.ftqq.com/)为准。未启用时不调用该通知服务。

不包含广告或另外的使用行为统计服务。本项目不向开发者另设的数据收集端发送遥测副本。

## 可选管理员终端

远程终端默认关闭，旧版配置不会自动开启。安装或修复时，用户可以单独勾选授权私人面板使用本次安装账号的管理员 PowerShell。该账号必须已经登录 Windows，并有可用的提升权限令牌；不会保存 Windows 密码，也不会以 SYSTEM 代替用户执行命令。授权账号注销或交互登录断开后不可用。

使用终端前，需要再次输入面板密码。终端输入、输出、执行账号、账号 SID、登录会话标识和窗口尺寸通过 HTTPS/WSS 经同一个面板服务器转发。服务器运营者可以接触这些数据，这不是端到端加密。命令能够读取或修改文件、系统设置以及启动程序；输出可能包含命令访问的文件或其他敏感内容。请只授权可信的私人面板。

项目不将终端命令和输出写入数据库或持久化会话记录；浏览器仅保留当前页面内的有限滚动缓冲。服务端日志记录电脑、会话、账号、创建/结束时间与结束原因等元信息。服务器运营者自行增加的代理日志或其他扩展有自己的保留行为。

切换电脑、离开终端页、主动结束、网页登录失效、网络断开、授权登录状态变化或服务停止会关闭会话并清理其进程组。闲置 5 分钟或连接 1 小时后也会结束。断线后不会排队或重放输入。通过新版安装向导的修复流程取消终端授权可关闭该功能；停止采集服务或卸载也会停止终端。已执行命令造成的系统变更不会因结束会话而撤销。

## 可选面板升级

面板升级默认关闭，必须在本机安装向导或 CLI 中单独授权。之后由面板所有者逐台确认目标版本并复验面板密码。采集服务只从同一个 HTTPS 服务下载固定摘要的产品安装包，核对大小、SHA-256、内嵌构建与载荷身份后，使用 SYSTEM 后台维护入口重启并更新本产品服务。这是软件维护授权，与以登录管理员账号运行用户命令的终端授权独立；不接受任意命令、执行参数或外部下载地址。

服务器按电脑保存最新升级任务、目标版本、创建/更新时间、阶段和结果，用于进度及恢复确认；移除电脑时也清除此记录。电脑本地会临时保存安装包与包含原配置/设备令牌的回退快照，放在受保护产品目录内，安装账号只读，管理员与 SYSTEM 可写。完成回执确认后清理临时安装器和快照；原版本程序目录保留供维护。回退失败或中断时保留快照，不会把未确认结果显示为成功。

在本机运行新版安装包修复，并取消“允许面板升级”，可停止后续任务领取；停止采集服务或卸载也会停止后续领取。已经开始的维护由独立进程完成或尝试回退，不能把停止服务视为取消已开始的安装。升级不会自动开启终端授权，不请求或保存 Windows 登录密码。签名状态以具体发布包说明为准，SHA-256 本身不是发布者签名。

## 联系与构建

开源项目问题可通过代码仓库的 Issues 联系维护者；不要公开发送任何监控数据或密钥。私有部署的数据问题请联系你的服务器运营者。源码修改、第三方再发布和自建服务器可能有不同的数据处理行为，请核对相应版本。

English summary: the agent sends authorized computer identity, hardware inventory, performance measurements, process names and resource usage, and health/queue information to the configured HTTPS server. The official installer targets the private service named above and requires its operator's pairing code. Installation validation starts real collection. Stop the Windows service or uninstall to stop transmission. Local queued samples may be uploaded after reconnection. Background monitoring does not collect screen contents, keystrokes, browser history, file contents, process command lines or communication payloads. An optional, locally authorized administrator terminal relays user input and command output through the same server over HTTPS/WSS, under the currently logged-in authorized Windows account, without storing its Windows password. The operator can access this content; it is not end-to-end encrypted. Commands may read or change system data. The project retains terminal lifecycle metadata, not command/output transcripts. Leaving the terminal, logout, disconnect or session expiry ends the process group; disconnected input is not replayed. Server operators control access, logs, retention and backups. Optional ServerChan notifications share alert content with that provider. See the Chinese sections above for details.
