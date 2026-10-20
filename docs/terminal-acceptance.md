# 管理员终端验收

本功能包含服务端、网页、新版 Windows 采集端和安装向导。本地协议测试使用明确的模拟采集端，不能替代下列 Windows 11 x64 实机验收。当前源码尚未完成这些实机项目，不应据此发布为已验收版本。

## 环境与授权

在专用测试电脑或可恢复的 Windows 虚拟机中使用一个已登录的管理员账号。使用与源码相同的新版安装包；先保持终端授权未勾选，验证常规采样正常且网页明确显示终端未授权。再次运行安装向导选择修复，阅读隐私说明并勾选终端授权。授权绑定本次安装账号 SID，保留原设备身份和监控历史。

服务端使用 HTTPS，反向代理放行模板中的终端 WebSocket 路径。面板登录成功后选择这台电脑，打开“远程终端”，再次输入面板密码。错误密码不能创建会话。成功后核对页面中的电脑 ID、账号与管理员状态。

## 在终端执行的只读检查

```powershell
whoami
whoami /user
whoami /groups
[Security.Principal.WindowsIdentity]::GetCurrent().IsSystem
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Get-Location
Set-Location $env:TEMP
Get-Location
[Console]::WriteLine('中文输出 😀')
```

账号必须等于本机授权账号，SYSTEM 为 False，Administrator 为 True，完整性级别为高；两次 `Get-Location` 之间应保留当前目录。检查中文、连续输出、复制、粘贴、调整窗口宽度与 Ctrl+C（例如中断 `Start-Sleep 30`）。终端输入不应被动画或实时监控刷新打断。

## 生命周期与电脑隔离

- 两台同名电脑分别建立会话，账号和命令输出不能串用。另一浏览器登录不能接管已有会话；同一电脑同时只允许一个会话。
- 在本机任务管理器记录终端 PowerShell PID，并启动一个测试子进程。分别测试“结束会话”、切换电脑、离开菜单、关闭网页、退出面板、采集端断网、停止服务和 Windows 账号注销。终端 PowerShell 及其进程组内的子进程均应退出，旧连接不能恢复或重放命令。
- Windows 账号注销或交互会话断开后不可连接；重新登录后需要新建终端会话。其他已登录账号不能替代原授权账号。
- 测试闲置 5 分钟、总连接 1 小时、浏览器连接后采集端未确认 20 秒的超时边界；超时后不得继续接收输入。
- 通过安装向导修复并取消授权，原会话应关闭，后续连接不可用；普通监控继续工作。旧安装包升级时默认不会开启终端。
- 让命令输出一段唯一的非敏感测试标记，检查项目服务日志和数据库只记录生命周期元信息、不含该命令或输出。不要在测试命令中使用真实密码或令牌。

记录实际操作系统、账号类型、本机授权状态、安装包 SHA-256、各步骤结果以及异常。真实验证失败时保留默认关闭状态，先定位原因，不用模拟结果替代。
