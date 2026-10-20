using System.Net.Http.Headers;
using System.Text.Json;
using System.Management;
using Howie.Monitor.Agent;
using Howie.Monitor.Collector;
using Howie.Monitor.Contracts;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = Host.CreateApplicationBuilder(args);
string configurationFile = Path.GetFullPath(builder.Configuration["config"] ?? ".local/agent.json");
var configuration = JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(configurationFile), ProtocolJson.Options)
    ?? throw new InvalidOperationException("采集配置不正确。");
var server = new Uri(configuration.ServerUrl);
if (server.Scheme != "https" && !(configuration.LocalDevelopment && server.IsLoopback))
    throw new InvalidOperationException("采集端正式上报必须使用 HTTPS，本地开发只允许环回地址。");
if (server.AbsolutePath != "/" || server.Query.Length > 0 || server.Fragment.Length > 0 || server.UserInfo.Length > 0)
    throw new InvalidOperationException("服务入口不能带路径、查询参数或凭据。");
string instanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configuration.DeviceId + ":" + configuration.Token)));
using var mutex = new Mutex(true, "Global\\HowieMonitor-" + instanceKey, out bool unique);
if (!unique) throw new InvalidOperationException("此设备的采集端已在运行。");
using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
using var bootRows = searcher.Get();
using var bootRow = bootRows.Cast<ManagementObject>().First();
string bootId = ManagementDateTimeConverter.ToDateTime((string)bootRow["LastBootUpTime"]).ToUniversalTime().Ticks.ToString();
var inventory = InventoryReader.Read();
var sensors = InventoryReader.ReadSensorEnvironment();
var state = new AgentState(configuration, new(configuration.DeviceId, Environment.MachineName, inventory,
    sensors, "0.1.0", bootId, Guid.NewGuid().ToString()));
builder.Services.AddWindowsService(options => options.ServiceName = "HowieMonitorAgent");
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Services.AddSingleton(state);
builder.Services.AddSingleton(new AgentQueue(Path.GetFullPath(configuration.DataDirectory)));
builder.Services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(4),
    DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", configuration.Token) } });
builder.Services.AddHostedService<CollectionWorker>();
builder.Services.AddHostedService<UploadWorker>();
builder.Services.AddHostedService<TerminalWorker>();
await builder.Build().RunAsync();
