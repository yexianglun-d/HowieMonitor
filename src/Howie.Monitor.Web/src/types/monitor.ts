export type MetricStatus = 'available' | 'notReady' | 'permissionDenied' | 'unsupported' | 'failed' | 'driverMissing' | 'unknown'
export interface Metric { key: string; deviceId: string; name: string; value: number | null; unit: string; source: string; status: MetricStatus; reason: string | null }
export interface ProcessReading { pid: number; startedAt: string | null; name: string; cpuPercent: number | null; workingSetBytes: number | null; status: MetricStatus; reason: string | null }
export interface Inventory {
  operatingSystem: string; osVersion: string
  cpus: { id: string; name: string; physicalCores: number; logicalProcessors: number }[]
  displayAdapters: { id: string; name: string; vendor: string; isHardwareGpu: boolean }[]
  physicalDisks: { id: string; index: number; name: string; sizeBytes: number | null }[]
  partitions: { id: string; diskIndex: number; sizeBytes: number | null }[]
  volumes: { id: string; fileSystem: string; sizeBytes: number | null; freeBytes: number | null; partitionIds: string[] }[]
  networks: { id: string; name: string; type: string; status: string }[]
}
export interface Device { deviceId: string; name: string; inventory: Inventory; sensors: { isAdministrator: boolean; pawnIoInstalled: boolean; pawnIoDeviceAccessible: boolean }; agentVersion: string }
export interface Sample { deviceId: string; sessionId: string; sequence: number; sampledAt: string; uptimeSeconds: number; collectionMilliseconds: number; metrics: Metric[]; processes: ProcessReading[] }
export interface Latest { sample: Sample | null; serverNow: string; receivedAt: string | null; lastHeartbeat: string | null; dataStatus: string; connectionStatus: string; agent: { queueLength: number; droppedSamples: number; lastError: string | null } | null }
export interface HistoryPoint { sampledAt: string; value: number | null; maximum?: number | null; coverageSeconds?: number }
export interface HistoryMetric { key: string; deviceId: string; name: string; unit: string }
export interface HistoryComputer { deviceId: string; name: string | null; isCurrent: boolean; firstSampleAt: string | null; lastSampleAt: string | null }
export interface SampleIdentity { sessionId: string; sequence: number }
export interface HistoryBucketPoint extends HistoryPoint {
  minimum: number | null; maximum: number | null; validSamples: number; totalSamples: number
  coverageSeconds: number; firstSampleAt: string | null; lastSampleAt: string | null; peakAt: string | null; peak: SampleIdentity | null
}
export interface HistorySeries {
  from: string; to: string; intervalSeconds: number; unit: string; points: HistoryBucketPoint[]
  average: number | null; minimum: number | null; maximum: number | null; validSamples: number; coverageSeconds: number
  firstSampleAt: string | null; lastSampleAt: string | null; peakAt: string | null; peak: SampleIdentity | null; clockErrorSamples: number
}
export interface HistoricalProcesses { sampledAt: string; processes: ProcessReading[] }
export interface AuthSession { authenticated: boolean; setupRequired: boolean }

export interface ComputerSummary {
  deviceId: string; name: string; dataStatus: string; connectionStatus: string; sampledAt: string | null; lastHeartbeat: string | null
  cpuPercent: number | null; memoryPercent: number | null; gpuPercent?: number | null; uptimeSeconds?: number | null
  temperatures?: Metric[] | null; lowestSpaceVolume?: string | null; lowestSpaceBytes?: number | null; serverNow?: string | null
}
