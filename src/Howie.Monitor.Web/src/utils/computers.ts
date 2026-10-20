import type { ComputerSummary } from '../types/monitor'

export function ageComputerSnapshot(computer: ComputerSummary, elapsedMilliseconds: number): ComputerSummary {
  const elapsed = Math.max(0, elapsedMilliseconds) / 1000
  const age = (at: string | null) => computer.serverNow && at ? (Date.parse(computer.serverNow) - Date.parse(at)) / 1000 + elapsed : elapsed
  const dataAge = age(computer.sampledAt)
  const dataStatus = ['fresh', 'delayed'].includes(computer.dataStatus)
    ? dataAge > 30 ? 'stale' : dataAge > 10 ? 'delayed' : computer.dataStatus : computer.dataStatus
  const connectionStatus = computer.connectionStatus === 'online' && age(computer.lastHeartbeat) > 30 ? 'disconnected' : computer.connectionStatus
  if (dataStatus === 'fresh') return { ...computer, connectionStatus }
  // 总览请求失败时，各台电脑的快照也会独立过期，旧值不能一直显示为当前读数。
  return { ...computer, dataStatus, connectionStatus, cpuPercent: null, gpuPercent: null, memoryPercent: null,
    uptimeSeconds: null, temperatures: [], lowestSpaceVolume: null, lowestSpaceBytes: null }
}
