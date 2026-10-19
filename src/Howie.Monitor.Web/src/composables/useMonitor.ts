import { computed, onMounted, onUnmounted, ref } from 'vue'
import * as api from '../api/monitor'
import { ApiError } from '../api/request'
import type { ComputerSummary, Device, Latest, Metric, HistoryPoint } from '../types/monitor'

export function useMonitor(initialComputerId = '') {
  const authenticated = ref(false), setupRequired = ref(false), initializing = ref(true), busy = ref(false)
  const connectionError = ref(''), authError = ref(''), device = ref<Device | null>(null), latest = ref<Latest | null>(null)
  const error = computed(() => authError.value || connectionError.value)
  const computers = ref<ComputerSummary[]>([]), selectedComputerId = ref(initialComputerId)
  const computerLoading = ref(false)
  const history = ref<Record<string, HistoryPoint[]>>({}), activeMetric = ref<Metric | null>(null), now = ref(Date.now())
  let timer: ReturnType<typeof setTimeout> | undefined, clock: ReturnType<typeof setInterval> | undefined
  let controller: AbortController | null = null, generation = 0, serverFetchedAt = 0
  const metrics = computed(() => latest.value?.sample?.metrics ?? [])
  const effectiveNow = computed(() => latest.value ? Date.parse(latest.value.serverNow) + now.value - serverFetchedAt : now.value)
  const dataStatus = computed(() => {
    if (!latest.value?.sample) return 'waiting'
    if (latest.value.dataStatus === 'clockError') return 'clockError'
    const age = (effectiveNow.value - Date.parse(latest.value.sample.sampledAt)) / 1000
    if (latest.value.dataStatus === 'stale' || age > 30) return 'stale'
    return age > 10 ? 'delayed' : 'fresh'
  })
  const connectionStatus = computed(() => latest.value?.connectionStatus === 'online' && latest.value.lastHeartbeat && effectiveNow.value - Date.parse(latest.value.lastHeartbeat) <= 30000 ? 'online' : 'disconnected')
  const find = (key: string) => metrics.value.find(metric => metric.key === key) ?? null
  const cpu = computed(() => find('cpu.usage'))
  const memory = computed(() => find('memory.usage'))
  const disk = computed(() => find('disk.activity'))
  const gpu = computed(() => metrics.value.find(metric => metric.deviceId.startsWith('sensor/Gpu') && metric.unit === '%' && ['D3D 3D', 'GPU Core'].includes(metric.name)) ?? null)
  const highlights = computed(() => [cpu.value, gpu.value, memory.value, disk.value])

  function stop() { generation++; clearTimeout(timer); controller?.abort(); controller = null }
  function clearComputer() { device.value = null; latest.value = null; history.value = {}; activeMetric.value = null }
  function selectComputer(id: string) {
    if (id === selectedComputerId.value || !computers.value.some(computer => computer.deviceId === id)) return
    // 切换先取消并清空旧快照，迟到响应由 generation 拒绝，避免混入另一台电脑的值。
    stop(); selectedComputerId.value = id; clearComputer(); computerLoading.value = true; connectionError.value = ''; void poll()
  }
  async function poll() {
    if (!authenticated.value) return
    const currentGeneration = generation
    const requestController = new AbortController(); controller = requestController
    if (!device.value) computerLoading.value = true
    try {
      const result = await api.getComputers(requestController.signal)
      if (currentGeneration !== generation) return
      computers.value = result
      const computerId = result.some(computer => computer.deviceId === selectedComputerId.value) ? selectedComputerId.value : result[0]?.deviceId ?? ''
      if (computerId !== selectedComputerId.value) { selectedComputerId.value = computerId; clearComputer() }
      if (!computerId) { clearComputer(); connectionError.value = ''; return }
      const [newDevice, newLatest] = await Promise.all([api.getDevice(computerId, requestController.signal), api.getLatest(computerId, requestController.signal)])
      if (currentGeneration !== generation) return
      device.value = newDevice; latest.value = newLatest; serverFetchedAt = Date.now(); connectionError.value = ''
      const tracked = [...highlights.value, activeMetric.value].filter((metric): metric is Metric => metric !== null)
      const histories = await Promise.all(tracked.map(async metric => [
        `${metric.deviceId}:${metric.key}`, await api.getHistory(computerId, metric.key, metric.deviceId, metric.unit, requestController.signal),
      ] as const))
      if (currentGeneration !== generation) return
      // 同一次采样的曲线一起提交，避免刷新时逐张卡片补齐造成视觉重排。
      history.value = { ...history.value, ...Object.fromEntries(histories) }
    } catch (err) {
      if (currentGeneration !== generation || (err instanceof DOMException && err.name === 'AbortError')) return
      if (err instanceof ApiError && err.status === 401) { authenticated.value = false; computers.value = []; selectedComputerId.value = ''; clearComputer() }
      connectionError.value = err instanceof Error ? err.message : '无法连接监控服务。'
    } finally {
      if (currentGeneration === generation) {
        computerLoading.value = false
        if (authenticated.value) timer = setTimeout(poll, document.hidden ? 10000 : 2000)
      }
    }
  }
  async function signIn(password: string) {
    busy.value = true; authError.value = ''
    try { await api.login(password, setupRequired.value); authenticated.value = true; setupRequired.value = false; stop(); await poll() }
    catch (err) { authError.value = err instanceof Error ? err.message : '登录失败。' }
    finally { busy.value = false }
  }
  async function signOut() {
    busy.value = true; authError.value = ''
    try { await api.logout(); stop(); authenticated.value = false; computers.value = []; selectedComputerId.value = ''; clearComputer(); history.value = {} }
    catch (err) { authError.value = err instanceof Error ? err.message : '退出失败，请重试。' }
    finally { busy.value = false }
  }
  function visibility() { if (authenticated.value) { stop(); void poll() } }
  onMounted(async () => {
    clock = setInterval(() => { now.value = Date.now() }, 1000)
    document.addEventListener('visibilitychange', visibility)
    try { const session = await api.getSession(); authenticated.value = session.authenticated; setupRequired.value = session.setupRequired; if (authenticated.value) await poll() }
    catch (err) { connectionError.value = err instanceof Error ? err.message : '服务暂不可用。' }
    finally { initializing.value = false }
  })
  onUnmounted(() => { stop(); clearInterval(clock); document.removeEventListener('visibilitychange', visibility) })
  return { computers, selectedComputerId, selectComputer, authenticated, setupRequired, initializing, computerLoading, busy, error, device, latest, metrics, history, activeMetric, dataStatus, connectionStatus, cpu, gpu, memory, disk, signIn, signOut, find }
}
