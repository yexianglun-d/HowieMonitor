namespace Howie.Monitor.Contracts;

public sealed record AgentConfiguration(string ServerUrl, string DeviceId, string Token, string DataDirectory, bool LocalDevelopment);
