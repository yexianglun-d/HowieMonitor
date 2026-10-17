using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32.SafeHandles;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Collector;

public static class InventoryReader
{
    public static Inventory Read()
    {
        List<CollectionIssue> issues = [];
        var os = Query("SELECT Caption,Version FROM Win32_OperatingSystem", issues).FirstOrDefault();
        var cpus = Query("SELECT DeviceID,Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor", issues)
            .Select(row => new CpuInfo(Text(row, "DeviceID"), Text(row, "Name"),
                Number<int>(row, "NumberOfCores"), Number<int>(row, "NumberOfLogicalProcessors"))).ToArray();
        var adapters = Query("SELECT DeviceID,Name,AdapterCompatibility,PNPDeviceID FROM Win32_VideoController", issues)
            .Select(row => new DisplayAdapterInfo(Text(row, "DeviceID"), Text(row, "Name"),
                Text(row, "AdapterCompatibility"), IsHardwareGpu(Text(row, "PNPDeviceID")))).ToArray();
        var disks = Query("SELECT DeviceID,Index,Model,Size FROM Win32_DiskDrive", issues)
            .Select(row => new DiskInfo(Text(row, "DeviceID"), Number<uint>(row, "Index"),
                Text(row, "Model"), Number<ulong>(row, "Size"))).ToArray();
        var partitions = Query("SELECT DeviceID,DiskIndex,Size FROM Win32_DiskPartition", issues)
            .Select(row => new PartitionInfo(Text(row, "DeviceID"), Number<uint>(row, "DiskIndex"),
                Number<ulong>(row, "Size"))).ToArray();
        var links = Query("SELECT Antecedent,Dependent FROM Win32_LogicalDiskToPartition", issues);
        var connections = Query("SELECT GUID FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL", issues)
            .Select(row => Text(row, "GUID")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var volumes = Query("SELECT DeviceID,FileSystem,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3", issues)
            .Select(row => new VolumeInfo(Text(row, "DeviceID"), Text(row, "FileSystem"),
                Number<ulong>(row, "Size"), Number<ulong>(row, "FreeSpace"),
                links.Where(link => ReferenceId(Text(link, "Dependent")) == Text(row, "DeviceID"))
                    .Select(link => ReferenceId(Text(link, "Antecedent"))).ToArray())).ToArray();
        List<NetworkInfo> networks = [];
        try
        {
            networks.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && connections.Contains(nic.Id))
                .Select(nic => new NetworkInfo(nic.Id, nic.Name, nic.NetworkInterfaceType.ToString(),
                    nic.OperationalStatus.ToString())));
        }
        catch (NetworkInformationException ex)
        {
            issues.Add(new("NetworkInterface", MetricStatus.Failed, ex.Message));
        }

        return new(Text(os, "Caption"), Text(os, "Version"), cpus, adapters, disks,
            partitions, volumes, networks, issues);
    }

    public static SensorEnvironment ReadSensorEnvironment()
    {
        List<CollectionIssue> issues = [];
        using var identity = WindowsIdentity.GetCurrent();
        bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var drivers = Query("SELECT Name,State,StartMode FROM Win32_SystemDriver "
            + "WHERE Name LIKE '%Pawn%' OR Name LIKE '%WinRing%'", issues)
            .Select(row => new DriverInfo(Text(row, "Name"), Text(row, "State"), Text(row, "StartMode"))).ToArray();
        bool installed = false;
        string? version = null;
        try
        {
            installed = PawnIo.IsInstalled;
            version = PawnIo.Version?.ToString();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            issues.Add(new("PawnIO registry", MetricStatus.PermissionDenied, ex.Message));
        }

        // 仅打开并关闭库所用的设备句柄，不加载模块、不安装或启动驱动。
        using var device = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, 0, 3, 0x80, 0);
        int? deviceError = device.IsInvalid ? Marshal.GetLastWin32Error() : null;
        return new(admin, installed, version, !device.IsInvalid, deviceError, drivers, issues);
    }

    public static bool IsHardwareGpu(string pnpId) =>
        pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase);

    private static List<Dictionary<string, object?>> Query(string query, List<CollectionIssue> issues)
    {
        List<Dictionary<string, object?>> rows = [];
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery(query), options);
            using var objects = searcher.Get();
            foreach (ManagementObject item in objects)
            {
                using (item)
                {
                    rows.Add(item.Properties.Cast<PropertyData>()
                        .ToDictionary(property => property.Name, property => (object?)property.Value,
                            StringComparer.OrdinalIgnoreCase));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            var status = ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80041003)
                || ex.HResult == unchecked((int)0x80070005)
                ? MetricStatus.PermissionDenied : MetricStatus.Failed;
            issues.Add(new(query, status, ex.Message));
        }

        return rows;
    }

    private static string Text(Dictionary<string, object?>? row, string key) =>
        row is not null && row.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static T? Number<T>(Dictionary<string, object?> row, string key) where T : struct =>
        row.TryGetValue(key, out var value) && value is not null
            ? (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture) : null;

    private static string ReferenceId(string path)
    {
        var match = Regex.Match(path, "DeviceID=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace(@"\\", @"\") : "";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing,
        nint security, uint creation, uint flags, nint template);
}
