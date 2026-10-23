using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Howie.Monitor.Agent;
using Xunit;

namespace Howie.Monitor.Server.Tests;

// 使用隔离 Windows runner 的当前账号，不安装服务、不连接面板或用户电脑。
public sealed class NativeTerminalTests
{
    [Fact(Timeout = 45000)]
    public async Task NativePowerShellStaysOpenAndRelaysOutputUntilExplicitExit()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.False(identity.IsSystem);
        Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        Assert.True(Process.GetCurrentProcess().SessionId > 0, "原生终端检查需要 runner 的已登录管理员会话，不能用 Session 0 替代。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var terminal = await WindowsAdminTerminal.Start(identity.User!.Value, 80, 24);
        var buffer = new byte[4096];
        var characters = new char[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        async Task Until(string expected)
        {
            var received = new StringBuilder();
            while (!received.ToString().Contains(expected, StringComparison.Ordinal))
            {
                int count = await terminal.Read(buffer, token);
                Assert.True(count > 0, "原生终端输出管道提前结束：" + received);
                received.Append(characters, 0, decoder.GetChars(buffer, 0, count, characters, 0));
                Assert.True(received.Length < 65536, "未收到预期终端输出。");
            }
        }
        await Until("PS ");
        var exited = terminal.WaitForExit(token);
        Assert.False(exited.IsCompleted, "PowerShell 在连接后即退出。");
        terminal.Resize(120, 30);
        string nonce = Guid.NewGuid().ToString("N");
        // 标记分段构造，不能把终端回显的命令本身当作执行结果。
        await terminal.Write("[Console]::WriteLine(('HOWIE_PIPE_' + '" + nonce + "'))\r", token);
        await Until("HOWIE_PIPE_" + nonce);
        Assert.False(exited.IsCompleted);
        await terminal.Write("exit\r", token);
        await exited.WaitAsync(token);
    }
}
