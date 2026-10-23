using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            int repliedThrough = 0;
            while (!received.ToString().Contains(expected, StringComparison.Ordinal))
            {
                int count;
                try { count = await terminal.Read(buffer, token); }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("等待终端输出 " + expected + " 超时，已收到：" + JsonSerializer.Serialize(received.ToString()));
                }
                Assert.True(count > 0, "原生终端输出管道提前结束：" + received);
                received.Append(characters, 0, decoder.GetChars(buffer, 0, count, characters, 0));
                Assert.True(received.Length < 65536, "未收到预期终端输出。");
                // 与浏览器终端一样回应能力与光标查询，不能让 PSReadLine 等待不存在的渲染端。
                foreach (Match query in Regex.Matches(received.ToString(), "\u001b\\[(?:\\??[56]n|[>0]?c)"))
                {
                    if (query.Index < repliedThrough) continue;
                    string response = query.Value switch
                    {
                        "\u001b[5n" => "\u001b[0n",
                        "\u001b[6n" => "\u001b[1;1R",
                        "\u001b[?6n" => "\u001b[?1;1R",
                        "\u001b[>c" => "\u001b[>0;0;0c",
                        _ => "\u001b[?1;2c"
                    };
                    await terminal.Write(response, token);
                    repliedThrough = query.Index + query.Length;
                }
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
