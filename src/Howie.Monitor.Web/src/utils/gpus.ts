import type { GpuSummary, Metric } from '../types/monitor'

export function gpuUsageAvailable(metric: Metric | null): metric is Metric & { value: number } {
  return metric !== null && metric.status === 'available' && metric.unit === '%' && metric.value !== null
    && Number.isFinite(metric.value) && metric.value >= 0 && metric.value <= 100
}
export function gpuUsageLabel(metric: Metric | null) {
  return metric?.name === 'D3D 3D' ? '3D 引擎占用' : metric?.name === 'GPU Core' ? '核心占用' : 'GPU 占用'
}
export function gpuSummaries(metrics: Metric[]): GpuSummary[] {
  const groups = new Map<string, Metric[]>()
  for (const metric of metrics) {
    if (!metric.deviceId.startsWith('sensor/Gpu') && metric.deviceId !== 'gpu') continue
    if (!groups.has(metric.deviceId)) groups.set(metric.deviceId, [])
    groups.get(metric.deviceId)!.push(metric)
  }
  return [...groups].sort(([a], [b]) => a.localeCompare(b)).map(([deviceId, readings]) => {
    // 先按显卡分组，再稳定选择核心指标；零值有效，显存/总线/功耗百分比不代表核心占用。
    const candidates = readings.filter(metric => metric.unit === '%' && ['GPU Core', 'D3D 3D', 'GPU 占用'].includes(metric.name))
    const priority = (metric: Metric) => ['GPU Core', 'D3D 3D', 'GPU 占用'].indexOf(metric.name)
    const usage = candidates.sort((a, b) => Number(gpuUsageAvailable(b)) - Number(gpuUsageAvailable(a))
      || priority(a) - priority(b) || a.key.localeCompare(b.key))[0] ?? null
    const source = usage?.source ?? [...readings].sort((a, b) => a.key.localeCompare(b.key))[0]!.source
    const name = /^LibreHardwareMonitorLib[^:]*:\s*(.+)\/[^/]+$/.exec(source)?.[1]?.trim() ?? deviceId
    return { deviceId, name, usage }
  })
}
