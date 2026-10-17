using Howie.Monitor.Collector;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Howie.Monitor.Server.Tests;

public sealed class RealCollectorEnrollmentTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "WindowsHardware")]
    public async Task RealWindowsSampleCanActivateAnIsolatedCandidate()
    {
        // 独立临时数据库与本次随机凭据，不读取或轮换正在运行的采集端配置。
        string directory = Path.Combine(Path.GetTempPath(), "howie-real-enrollment-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TelemetryStore(directory);
            var original = new DeviceCredential("original-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            store.InitializeEnrollment(original, new EphemeralDataProtectionProvider().CreateProtector("real-enrollment-test"));
            var code = store.CreateEnrollmentCode(new(false, original.DeviceId), DateTimeOffset.UtcNow).Code!;
            string request = Guid.NewGuid().ToString();
            var candidate = store.ClaimEnrollment(new(code.Code, request, Environment.MachineName), DateTimeOffset.UtcNow).Credential!;
            var inventory = InventoryReader.Read(); string session = Guid.NewGuid().ToString();
            var registration = new DeviceRegistration(candidate.DeviceId, Environment.MachineName, inventory,
                InventoryReader.ReadSensorEnvironment(), "0.1.0", "isolated-real-boot", session);
            string authorization = "Bearer " + candidate.Token;
            Assert.Null(store.RegisterAuthenticated(authorization, registration, DateTimeOffset.UtcNow).Error);
            using var collector = new WindowsSystemCollector(inventory);
            collector.Collect(); await Task.Delay(2000);
            var readings = collector.Collect();
            var sample = new TelemetrySample(candidate.DeviceId, registration.BootId, session, 1,
                DateTimeOffset.UtcNow, Environment.TickCount64 / 1000d, 0, readings.Metrics, [], readings.Issues);
            Assert.Null(store.BeatAuthenticated(authorization, new(candidate.DeviceId, registration.BootId, session, 1, 0, 0, null), DateTimeOffset.UtcNow).Error);
            Assert.NotNull(store.IngestAuthenticated(authorization, new(false, [sample]), DateTimeOffset.UtcNow).Acknowledgement);
            Assert.Equal("ready", store.EnrollmentStatus(authorization, DateTimeOffset.UtcNow)!.State);
            Assert.Equal(0, store.SampleCount());
            Assert.Equal("active", store.ActivateEnrollment(authorization, request, DateTimeOffset.UtcNow).Progress!.State);
            Assert.Equal("fresh", store.Latest(DateTimeOffset.UtcNow).DataStatus);
            Assert.False(store.AuthenticateIngest("Bearer " + original.Token, DateTimeOffset.UtcNow));
            output.WriteLine("Actual computer: " + Environment.MachineName);
            foreach (var reading in readings.Metrics.Where(value => value.Key is "cpu.usage" or "memory.usage"))
                output.WriteLine($"{reading.Key}: {reading.Value} {reading.Unit}; {reading.Source}; {reading.Status}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
