import { computed, onUnmounted, ref, watch } from 'vue'
import * as api from '../../../api/monitor'
import type { HistoricalProcesses, HistoryBucketPoint, HistoryComputer, HistoryMetric, HistorySeries } from '../../../types/monitor'

export function useHistory(owner: () => string | undefined) {
  const computers = ref<HistoryComputer[]>([]), selectedComputerId = ref(''), computerLoading = ref(true)
  const selectedComputer = computed(() => computers.value.find(computer => computer.deviceId === selectedComputerId.value) ?? null)
  const metrics = ref<HistoryMetric[]>([]), selectedId = ref(''), hours = ref(24)
  const catalogLoading = ref(true), loading = ref(false), processLoading = ref(false)
  const error = ref(''), processError = ref(''), series = ref<HistorySeries | null>(null), processes = ref<HistoricalProcesses | null>(null)
  const selectedPoint = ref<HistoryBucketPoint | null>(null)
  const identity = (metric: HistoryMetric) => JSON.stringify([metric.deviceId, metric.key, metric.unit])
  const selectedMetric = computed(() => metrics.value.find(metric => identity(metric) === selectedId.value) ?? null)
  const observedPoints = computed(() => series.value?.points.filter(point => point.totalSamples > 0) ?? [])
  let queryController: AbortController | null = null, processController: AbortController | null = null
  let queryGeneration = 0, processGeneration = 0, catalogGeneration = 0
  let catalogController: AbortController | null = null
  let computerController: AbortController | null = null, computerGeneration = 0

  async function loadComputers() {
    const current = ++computerGeneration; computerController?.abort(); computerController = new AbortController()
    computerLoading.value = true; error.value = ''
    try {
      const result = await api.getHistoryComputers(computerController.signal)
      if (current !== computerGeneration) return
      computers.value = result
      const previous = selectedComputerId.value
      const preferred = owner()
      selectedComputerId.value = result.some(computer => computer.deviceId === previous) ? previous
        : result.some(computer => computer.deviceId === preferred) ? preferred!
        : (result.find(computer => computer.isCurrent) ?? result[0])?.deviceId ?? ''
      if (previous === selectedComputerId.value) await loadCatalog()
    } catch (err) {
      if (current !== computerGeneration || err instanceof DOMException && err.name === 'AbortError') return
      error.value = err instanceof Error ? err.message : '历史电脑加载失败。'
    } finally { if (current === computerGeneration) { computerLoading.value = false; if (!selectedComputerId.value) catalogLoading.value = false } }
  }

  async function selectPoint(point: HistoryBucketPoint | null) {
    const current = ++processGeneration; processController?.abort(); processController = new AbortController()
    selectedPoint.value = point; processes.value = null; processError.value = ''; processLoading.value = false
    const computerId = selectedComputerId.value
    if (!point?.peak || !computerId) return
    processLoading.value = true
    try {
      const result = await api.getHistoryProcesses(computerId, point.peak, processController.signal)
      if (current === processGeneration && computerId === selectedComputerId.value) processes.value = result
    } catch (err) {
      if (current !== processGeneration || err instanceof DOMException && err.name === 'AbortError') return
      processError.value = err instanceof Error ? err.message : '历史进程查询失败。'
    } finally { if (current === processGeneration) processLoading.value = false }
  }

  async function refresh() {
    const current = ++queryGeneration; queryController?.abort(); queryController = new AbortController()
    series.value = null; error.value = ''; loading.value = false; void selectPoint(null)
    const metric = selectedMetric.value, computerId = selectedComputerId.value
    if (!metric || !computerId) return
    loading.value = true
    try {
      const result = await api.getHistorySeries(computerId, metric, hours.value, queryController.signal)
      // 快速切换指标或范围时，仅最后一次请求可以更新页面。
      if (current !== queryGeneration || computerId !== selectedComputerId.value) return
      series.value = result
      await selectPoint(result.peak ? result.points.find(point => point.peak?.sessionId === result.peak!.sessionId && point.peak?.sequence === result.peak!.sequence) ?? null : null)
    } catch (err) {
      if (current !== queryGeneration || err instanceof DOMException && err.name === 'AbortError') return
      error.value = err instanceof Error ? err.message : '历史查询失败。'
    } finally { if (current === queryGeneration) loading.value = false }
  }

  async function loadCatalog() {
    const current = ++catalogGeneration; catalogController?.abort(); catalogController = new AbortController()
    const computerId = selectedComputerId.value, previous = selectedId.value
    queryGeneration++; queryController?.abort(); series.value = null; metrics.value = []; selectedId.value = ''; void selectPoint(null)
    loading.value = false
    catalogLoading.value = true; error.value = ''
    if (!computerId) { catalogLoading.value = false; return }
    try {
      const result = await api.getHistoryMetrics(computerId, catalogController.signal)
      if (current !== catalogGeneration || computerId !== selectedComputerId.value) return
      metrics.value = result
      const initial = metrics.value.find(metric => identity(metric) === previous) ?? metrics.value.find(metric => metric.key === 'cpu.usage') ?? metrics.value[0]
      selectedId.value = initial ? identity(initial) : ''
    } catch (err) {
      if (current !== catalogGeneration || err instanceof DOMException && err.name === 'AbortError') return
      error.value = err instanceof Error ? err.message : '历史指标加载失败。'
    } finally { if (current === catalogGeneration) catalogLoading.value = false }
  }
  watch([selectedId, hours], refresh)
  watch(selectedComputerId, loadCatalog)
  watch(owner, loadComputers, { immediate: true })
  onUnmounted(() => { queryGeneration++; processGeneration++; catalogGeneration++; computerGeneration++; computerController?.abort(); catalogController?.abort(); queryController?.abort(); processController?.abort() })
  return { computers, selectedComputerId, selectedComputer, computerLoading, metrics, selectedId, hours, selectedMetric, catalogLoading, loading, processLoading, error, processError, series,
    processes, selectedPoint, observedPoints, identity, refresh, loadComputers, selectPoint }
}
