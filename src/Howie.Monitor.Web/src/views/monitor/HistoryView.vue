<script setup lang="ts">
import { ElButton, ElCollapse, ElCollapseItem, ElSelect, ElOption } from 'element-plus'
import { computed, nextTick } from 'vue'
import TrendChart from '../../components/base/TrendChart.vue'
import ProcessTable from './components/ProcessTable.vue'
import { useHistory } from './composables/useHistory'
import { capacity, number, rate } from '../../utils/format'
import type { HistoryMetric, HistoryBucketPoint } from '../../types/monitor'

const props = defineProps<{ deviceId?: string; label: (metric: HistoryMetric) => string }>()
const { computers, selectedComputerId, selectedComputer, computerLoading, metrics, selectedId, hours, selectedMetric, catalogLoading, loading, processLoading, error, processError, series,
  processes, selectedPoint, observedPoints, identity, loadComputers, selectPoint } = useHistory(() => props.deviceId)
const formatDate = (value: string | null | undefined) => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
function metricLabel(metric: HistoryMetric) {
  if (selectedComputer.value?.deviceId === props.deviceId) return props.label(metric)
  // 旧电脑没有完整硬件清单快照，使用当时保存的指标名称及设备标识，不能套用当前硬件型号。
  if (metric.deviceId === 'cpu/total') return `CPU 总览 · ${metric.name}`
  if (metric.deviceId === 'memory') return `物理内存 · ${metric.name}`
  if (metric.deviceId.startsWith('cpu/logical/')) return `逻辑处理器 ${metric.deviceId.split('/').at(-1)} · ${metric.name}`
  return `${metric.name} · ${metric.deviceId}`
}
function valueText(value: number | null | undefined) {
  if (value === null || value === undefined) return '—'
  const unit = selectedMetric.value?.unit
  return unit === 'B' ? capacity(value) : unit === 'B/s' ? rate(value) : `${number(value)} ${unit ?? ''}`
}
const coverage = computed(() => {
  if (!series.value) return '—'
  const duration = (Date.parse(series.value.to) - Date.parse(series.value.from)) / 1000
  return `${number(duration > 0 ? Math.min(100, series.value.coverageSeconds / duration * 100) : 0)} %`
})
async function inspectPoint(point: HistoryBucketPoint) { void selectPoint(point); await nextTick(); document.getElementById('peak-processes')?.scrollIntoView({ block: 'start', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' }) }
const tablePoints = computed(() => [...observedPoints.value].reverse())
</script>

<template>
  <section class="panel history-controls" data-enter>
    <div class="history-field"><label for="history-computer">查询电脑</label><ElSelect id="history-computer" v-model="selectedComputerId" :disabled="computerLoading || !computers.length"><ElOption v-for="computer in computers" :key="computer.deviceId" :value="computer.deviceId" :label="(computer.name ?? '未记录名称') + ' · ' + (computer.deviceId === props.deviceId ? '最近查看' : '查询电脑 · ' + computer.deviceId.slice(-8))"/></ElSelect></div>
    <div class="history-field"><label for="history-metric">指标</label><ElSelect filterable id="history-metric" v-model="selectedId" :disabled="catalogLoading || !metrics.length"><ElOption v-for="metric in metrics" :key="identity(metric)" :value="identity(metric)" :label="metricLabel(metric) + ' · ' + metric.unit"/></ElSelect></div>
    <div class="history-ranges" aria-label="历史时间范围"><button v-for="range in [{ hours: 1, label: '最近 1 小时' }, { hours: 24, label: '最近 24 小时' }, { hours: 168, label: '最近 7 天' }]" :key="range.hours" :class="{ active: hours === range.hours }" @click="hours = range.hours">{{ range.label }}</button></div>
    <ElButton type="primary" link :disabled="loading || catalogLoading || computerLoading" @click="loadComputers">刷新</ElButton>
  </section>
  <p v-if="selectedComputer && selectedComputer.deviceId !== props.deviceId" class="state-banner" role="status">正在查看 {{ selectedComputer.name ?? '未记录名称的电脑' }} 的历史。保留期内记录：{{ formatDate(selectedComputer.firstSampleAt) }} — {{ formatDate(selectedComputer.lastSampleAt) }}；不代表该电脑当前仍在上报。</p>
  <p v-if="computerLoading || catalogLoading || loading" class="empty-state" role="status">正在查询历史数据…</p>
  <section v-else-if="error" class="panel empty-state"><p>历史数据未加载</p><ElButton @click="loadComputers">重新查询</ElButton></section>
  <p v-else-if="!metrics.length && !error" class="panel empty-state">这台电脑暂无历史记录。</p>
  <template v-else-if="series">
    <div class="history-summary" data-enter><section class="panel"><span>平均值</span><strong>{{ valueText(series.average) }}</strong></section><section class="panel"><span>最高值</span><strong>{{ valueText(series.maximum) }}</strong><small>{{ formatDate(series.peakAt) }}</small></section><section class="panel"><span>有效采样</span><strong>{{ series.validSamples.toLocaleString() }}</strong><small>覆盖 {{ coverage }}</small></section></div>
    <section class="panel history-chart" data-enter><div class="panel-heading"><div><h2>{{ selectedMetric ? metricLabel(selectedMetric) : '历史变化' }}</h2><p>{{ series.intervalSeconds === 2 ? '采样级' : `${series.intervalSeconds / 60} 分钟汇总` }} · {{ formatDate(series.from) }} — {{ formatDate(series.to) }}</p></div><div class="history-legend"><span>● 均值</span><span>┄ 峰值</span></div></div>
      <TrendChart :points="series.points" :percent="series.unit === '%'" :key="selectedId + hours" :unit="series.unit" :interval-seconds="series.intervalSeconds" show-maximum date-time/>
      <ElCollapse class="help-details"><ElCollapseItem title="采样说明" name="details"><p>覆盖率按 2 秒采样间隔估算。空缺断开显示，缺失值不参与均值。圆点表示该时段只有部分采样。有效数据：{{ formatDate(series.firstSampleAt) }} 至 {{ formatDate(series.lastSampleAt) }}。</p></ElCollapseItem></ElCollapse>
      <p v-if="series.clockErrorSamples" class="state-banner">范围内有 {{ series.clockErrorSamples }} 条采样时间异常，未计入有效值统计。</p>
      <p v-if="!series.validSamples" class="empty-state">该范围没有这个指标的有效数据。</p>
    </section>
    <section id="peak-processes" class="panel"><div class="panel-heading"><div><h2>峰值时的主要进程</h2><p>{{ formatDate(processes?.sampledAt ?? selectedPoint?.peakAt) }} · CPU / 内存各前 5 项，已去重</p></div><strong class="detail-value">{{ valueText(selectedPoint?.maximum) }}</strong></div>
      <div v-if="processError" class="empty-state"><p>进程数据未加载</p><ElButton type="primary" link @click="selectPoint(selectedPoint)">重新读取</ElButton></div><p v-else-if="processLoading" class="empty-state" role="status">正在读取当时的进程…</p>
      <template v-else-if="processes"><ProcessTable :processes="processes.processes"/><p class="footnote">进程占用仅供对照，不能单独用来判断指标变化的原因。</p></template>
      <p v-else class="empty-state">选择下方时段查看峰值对应的进程。</p>
    </section>
    <section class="panel"><div class="panel-heading"><div><h2>采样明细</h2><p>选择时段查看峰值时的进程。</p></div></div><p class="scroll-hint">左右滑动查看完整明细</p><div class="table-scroll history-table" tabindex="0" aria-label="采样明细，可左右滚动"><table><thead><tr><th>时间</th><th>均值</th><th>最高值</th><th>有效采样</th><th>覆盖</th></tr></thead><tbody><tr v-for="point in tablePoints" :key="point.sampledAt" :class="{ selected: selectedPoint?.sampledAt === point.sampledAt }"><td>{{ formatDate(point.sampledAt) }}<ElButton type="primary" link :disabled="!point.peak" @click="inspectPoint(point)" class="metric-link">查看进程</ElButton></td><td>{{ valueText(point.value) }}</td><td>{{ valueText(point.maximum) }}</td><td>{{ point.validSamples }}</td><td>{{ point.coverageSeconds }} / {{ series.intervalSeconds }} 秒</td></tr></tbody></table></div><p v-if="!tablePoints.length" class="empty-state">此范围尚无记录。</p></section>
  </template>
</template>
