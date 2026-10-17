namespace Howie.Monitor.Server;

public static class AdminInitialization
{
    public static int Run(string dataDirectory)
    {
        var credentials = new CredentialStore(dataDirectory);
        if (credentials.IsSetup) { Console.Error.WriteLine("访问账号已初始化，未修改现有密码。"); return 1; }
        Console.Write("设置访问密码（12–128 位）：");
        string password = ReadPassword();
        Console.Write("再次输入：");
        string confirmation = ReadPassword();
        if (password != confirmation || !credentials.Initialize(password))
        { Console.Error.WriteLine("初始化失败：两次密码必须一致，且为 12–128 位。"); return 1; }
        Console.WriteLine("访问账号已初始化。请保护数据目录并启动服务。"); return 0;
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return password.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
            else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
    }
}
