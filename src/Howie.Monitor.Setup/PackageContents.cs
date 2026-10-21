using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace Howie.Monitor.Setup;

internal static class PackageContents
{
    internal static string PayloadHash => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(value => value.Key == "AgentPayloadSha256").Value ?? "";

    internal static Stream OpenPayload()
    {
        string hash = PayloadHash;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidOperationException("安装载荷未提供，请使用正式安装包。");
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Howie.Monitor.Setup.Agent.zip")
            ?? throw new InvalidOperationException("安装载荷不存在，请使用完整安装包。");
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
        { stream.Dispose(); throw new InvalidOperationException("安装载荷校验失败。"); }
        stream.Position = 0; return stream;
    }
    private static Stream OpenNotices() => Assembly.GetExecutingAssembly().GetManifestResourceStream("Howie.Monitor.Setup.Notices.zip")
        ?? throw new InvalidOperationException("第三方许可载荷未提供，请使用正式安装包。");

    internal static object Check()
    {
        using var payload = OpenPayload(); using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        if (archive.Entries.Count(entry => entry.Name == "Howie.Monitor.Agent.exe") != 1
            || !archive.Entries.Any(entry => entry.Name == "MPL-2.0.txt")
            || !archive.Entries.Any(entry => entry.Name == "THIRD-PARTY-NOTICES.md"))
            throw new InvalidOperationException("采集程序或第三方许可不完整。");
        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Replace('\\', '/').Split('/');
            if (parts.Any(part => part == ".." || part.Contains(':')) || parts[0].Length == 0
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || new[] { "agent.json", "user.json", "device-credential.json", "monitor.db", "notifications.json" }.Contains(entry.Name))
                throw new InvalidOperationException("安装包包含无效路径或私有配置。");
        }
        using var notices = OpenNotices(); using var noticeArchive = new ZipArchive(notices, ZipArchiveMode.Read);
        if (!noticeArchive.Entries.Any(entry => entry.Name == "THIRD-PARTY-NOTICES.md")
            || !noticeArchive.Entries.Any(entry => entry.Name.StartsWith("microsoft.windowsdesktop.app-", StringComparison.Ordinal)))
            throw new InvalidOperationException("安装向导运行时许可不完整。");
        return new { product = SetupPaths.Product, version = Howie.Monitor.Contracts.AgentBuild.Current.Version,
            buildId = Howie.Monitor.Contracts.AgentBuild.Current.BuildId, updateProtocol = 1, runtime = Environment.Version.ToString(), agentSha256 = PayloadHash,
            agentFiles = archive.Entries.Count, noticeFiles = noticeArchive.Entries.Count, server = SetupPaths.ServerUrl };
    }

    internal static void InstallNotices()
    {
        using var notices = OpenNotices(); using var archive = new ZipArchive(notices, ZipArchiveMode.Read);
        string root = Path.Combine(SetupPaths.ProgramRoot, "setup-notices");
        foreach (var entry in archive.Entries)
        {
            string target = Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            SetupPaths.VerifyPath(target, root);
            if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, true); }
        }
    }

    internal static void ShowNotices(Form owner)
    {
        try
        {
            var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using var payload = OpenPayload(); using var agent = new ZipArchive(payload, ZipArchiveMode.Read);
            using var notices = OpenNotices(); using var setup = new ZipArchive(notices, ZipArchiveMode.Read);
            foreach (var group in new[] { (Name: "Agent", Archive: agent), (Name: "Setup", Archive: setup) })
                foreach (var entry in group.Archive.Entries.Where(value => value.Name.EndsWith(".txt") || value.Name is "LICENSE" or "THIRD-PARTY-NOTICES.md"))
                { using var reader = new StreamReader(entry.Open()); texts[group.Name + " / " + entry.Name] = reader.ReadToEnd(); }
            using var dialog = new Form { Text = "第三方组件与许可", Size = new(850, 650), StartPosition = FormStartPosition.CenterParent };
            var choice = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            var content = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            choice.Items.AddRange(texts.Keys.Cast<object>().ToArray());
            choice.SelectedIndexChanged += (_, _) => content.Text = texts[(string)choice.SelectedItem!];
            dialog.Controls.Add(content); dialog.Controls.Add(choice); choice.SelectedIndex = 0; dialog.ShowDialog(owner);
        }
        catch (InvalidOperationException ex) { MessageBox.Show(owner, ex.Message, "Howie Monitor"); }
    }

    internal static void ShowPrivacy(Form owner)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Howie.Monitor.Setup.Privacy.md")
            ?? throw new InvalidOperationException("隐私说明不存在，请使用完整安装包。");
        using var reader = new StreamReader(stream);
        using var dialog = new Form { Text = "Howie Monitor · 隐私说明", Size = new(850, 650), StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Text = reader.ReadToEnd(), Font = new("Microsoft YaHei UI", 10) });
        dialog.ShowDialog(owner);
    }
}
