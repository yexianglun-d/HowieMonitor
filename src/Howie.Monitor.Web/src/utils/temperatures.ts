import type { Metric } from '../types/monitor'

export type TemperatureCategory = 'cpu' | 'gpu' | 'disk' | 'other'
export interface TemperatureGroup { id: string; category: TemperatureCategory; title: string; hardware: string; readings: Metric[]; primary: Metric | null }
const titles = { cpu: 'CPU 温度', gpu: 'GPU 温度', disk: '磁盘温度', other: '硬件温度' }
const labels: Record<string, string> = {
  'core (tctl/tdie)': 'CPU 核心温度', 'cpu package': 'CPU 封装温度', 'core max': 'CPU 最高核心温度',
  'gpu core': 'GPU 核心温度', 'gpu hot spot': 'GPU 热点温度', 'gpu memory junction': 'GPU 显存结温', 'gpu vr soc': 'GPU SoC 温度',
  'composite temperature': '磁盘综合温度', temperature: '温度',
  'warning temperature': '告警温度阈值', 'critical temperature': '临界温度阈值',
}
function category(metric: Metric): TemperatureCategory {
  if (/^(cpu(?:\/|$)|sensor\/Cpu\/)/.test(metric.deviceId)) return 'cpu'
  if (/^(gpu$|sensor\/Gpu[^/]*\/)/.test(metric.deviceId)) return 'gpu'
  if (/^(disk$|sensor\/Storage\/)/.test(metric.deviceId)) return 'disk'
  return 'other'
}
export function temperatureLabel(metric: Metric) {
  const name = metric.name.trim(), indexed = /^Temperature #(\d+)$/i.exec(name)
  return labels[name.toLowerCase()] ?? (indexed ? '温度传感器 ' + indexed[1] : /温度|temperature/i.test(name) ? name : name + ' 温度')
}
export function temperatureAvailable(metric: Metric | null): metric is Metric & { value: number } {
  return metric !== null && metric.status === 'available' && metric.value !== null && Number.isFinite(metric.value)
}
export function temperatureThreshold(metric: Metric) {
  // LibreHardwareMonitor 的这两项来自 SMART 告警/临界阈值，不能作为磁盘当前温度。
  return metric.unit === '°C' && category(metric) === 'disk' && /^(Warning|Critical) Temperature$/i.test(metric.name.trim())
}
function priority(metric: Metric) {
  return /^(Core \(Tctl\/Tdie\)|CPU Package|GPU Core|Composite Temperature|Temperature)$/i.test(metric.name.trim()) ? 0 : 1
}
export function temperatureGroups(metrics: Metric[]) {
  const groups = new Map<string, TemperatureGroup>()
  for (const metric of metrics) {
    if (metric.unit !== '°C' || temperatureThreshold(metric)) continue
    const kind = category(metric)
    if (!groups.has(metric.deviceId)) {
      const source = /^LibreHardwareMonitorLib[^:]*:\s*(.+)\/Temperature$/.exec(metric.source)
      groups.set(metric.deviceId, { id: metric.deviceId, category: kind, title: titles[kind], hardware: source?.[1]?.trim() ?? titles[kind], readings: [], primary: null })
    }
    groups.get(metric.deviceId)!.readings.push(metric)
  }
  for (const group of groups.values()) {
    const available = group.readings.filter(temperatureAvailable)
    group.primary = [...(available.length ? available : group.readings)].sort((a, b) => priority(a) - priority(b))[0] ?? null
  }
  for (const kind of ['cpu', 'gpu', 'disk'] as const) {
    if (![...groups.values()].some(group => group.category === kind))
      groups.set('missing/' + kind, { id: 'missing/' + kind, category: kind, title: titles[kind], hardware: '尚无温度上报', readings: [], primary: null })
  }
  const order = { cpu: 0, gpu: 1, disk: 2, other: 3 }
  return [...groups.values()].sort((a, b) => order[a.category] - order[b.category] || a.id.localeCompare(b.id))
}
export function temperatureReason(group: TemperatureGroup) {
  if (!group.primary) return '采集端尚未上报这类温度。'
  if (group.category === 'cpu' && group.primary.status === 'driverMissing') return '未检测到读取 CPU 温度所需的 PawnIO 驱动。'
  if (group.primary.status === 'permissionDenied') return '当前采集端没有读取该传感器的权限。'
  return group.primary.reason ?? '传感器没有返回有效温度。'
}
