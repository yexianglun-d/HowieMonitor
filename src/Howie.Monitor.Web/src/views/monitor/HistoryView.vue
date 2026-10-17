<script setup lang="ts">
import { computed } from 'vue'
import TrendChart from '../../components/base/TrendChart.vue'
import ProcessTable from './components/ProcessTable.vue'
import { useHistory } from './composables/useHistory'
import { capacity, number, rate } from '../../utils/format'
import type { HistoryMetric } from '../../types/monitor'

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
const tablePoints = computed(() => [...observedPoints.value].reverse())
</script>

<template>
  <section class="panel history-controls">
    <div class="history-field"><label for="history-computer">历史电脑</label><select id="history-computer" v-model="selectedComputerId" :disabled="computerLoading || !computers.length"><option v-for="computer in computers" :key="computer.deviceId" :value="computer.deviceId">{{ computer.name ?? '未记录名称' }} · {{ computer.deviceId === props.deviceId ? '当前选择' : `历史电脑 · ${computer.deviceId.slice(-8)}` }}</option></select></div>
    <div class="history-field"><label for="history-metric">历史指标</label><select id="history-metric" v-model="selectedId" :disabled="catalogLoading || !metrics.length"><option v-for="metric in metrics" :key="identity(metric)" :value="identity(metric)">{{ metricLabel(metric) }} · {{ metric.unit }}</option></select></div>
    <div class="history-ranges" aria-label="历史时间范围"><button v-for="range in [{ hours: 1, label: '最近 1 小时' }, { hours: 24, label: '最近 24 小时' }, { hours: 168, label: '最近 7 天' }]" :key="range.hours" :class="{ active: hours === range.hours }" @click="hours = range.hours">{{ range.label }}</button></div>
    <button class="text-button" :disabled="loading || catalogLoading || computerLoading" @click="loadComputers">刷新查询 ↻</button>
  </section>
  <p v-if="selectedComputer && selectedComputer.deviceId !== props.deviceId" class="state-banner" role="status">正在查看 {{ selectedComputer.name ?? '未记录名称的电脑' }} 的历史。保留期内记录：{{ formatDate(selectedComputer.firstSampleAt) }} — {{ formatDate(selectedComputer.lastSampleAt) }}；不代表该电脑当前仍在上报。</p>
  <p v-if="error" class="error-banner" role="alert">{{ error }}</p>
  <p v-if="computerLoading || catalogLoading || loading" class="empty-state" role="status">正在查询历史数据…</p>
  <p v-else-if="!metrics.length && !error" class="panel empty-state">尚无历史记录，接入后会随真实采样持续积累。</p>
  <template v-else-if="series">
    <div class="history-summary"><section class="panel"><span>范围均值</span><strong>{{ valueText(series.average) }}</strong></section><section class="panel"><span>最高值</span><strong>{{ valueText(series.maximum) }}</strong><small>{{ formatDate(series.peakAt) }}</small></section><section class="panel"><span>有效采样</span><strong>{{ series.validSamples.toLocaleString() }}</strong><small>覆盖 {{ coverage }} · 按两秒采样槽估算</small></section></div>
    <section class="panel history-chart"><div class="panel-heading"><div><h2>{{ selectedMetric ? metricLabel(selectedMetric) : '历史变化' }}</h2><p>{{ series.intervalSeconds === 2 ? '采样级' : `${series.intervalSeconds / 60} 分钟汇总` }} · {{ formatDate(series.from) }} — {{ formatDate(series.to) }}</p></div><div class="history-legend"><span>● 均值</span><span>┄ 峰值</span></div></div>
      <TrendChart :points="series.points" :percent="series.unit === '%'" :interval-seconds="series.intervalSeconds" show-maximum/>
      <div class="chart-time"><span>{{ formatDate(series.from) }}</span><span>{{ formatDate(series.to) }}</span></div>
      <p class="footnote">有效数据：{{ formatDate(series.firstSampleAt) }} — {{ formatDate(series.lastSampleAt) }}。空缺断开显示，缺失值不参与均值；圆点表示该时间桶未覆盖完整采样时段。</p>
      <p v-if="series.clockErrorSamples" class="state-banner">范围内有 {{ series.clockErrorSamples }} 条采样时间异常，未计入有效值统计。</p>
      <p v-if="!series.validSamples" class="empty-state">该范围没有这个指标的有效数据。</p>
    </section>
    <section class="panel"><div class="panel-heading"><div><h2>峰值时的主要进程</h2><p>{{ formatDate(processes?.sampledAt ?? selectedPoint?.peakAt) }} · 当时 CPU 与内存各前 5 项，重复项合并</p></div><strong class="detail-value">{{ valueText(selectedPoint?.maximum) }}</strong></div>
      <p v-if="processError" class="error-banner" role="alert">{{ processError }}</p><p v-else-if="processLoading" class="empty-state" role="status">正在读取当时的进程…</p>
      <ProcessTable v-else-if="processes" :processes="processes.processes"/>
      <p v-else class="empty-state">选择下方时间桶查看峰值对应的进程。</p>
    </section>
    <section class="panel"><div class="panel-heading"><div><h2>时间桶明细</h2><p>选择一行查看该桶最高值对应的真实进程；进程占用不代表它是该指标变化的唯一原因。</p></div></div><div class="table-scroll history-table"><table><thead><tr><th>时间</th><th>均值</th><th>最高值</th><th>有效采样</th><th>覆盖</th><th></th></tr></thead><tbody><tr v-for="point in tablePoints" :key="point.sampledAt" :class="{ selected: selectedPoint?.sampledAt === point.sampledAt }"><td>{{ formatDate(point.sampledAt) }}</td><td>{{ valueText(point.value) }}</td><td>{{ valueText(point.maximum) }}</td><td>{{ point.validSamples }}</td><td>{{ point.coverageSeconds }} / {{ series.intervalSeconds }} 秒</td><td><button class="metric-link" :disabled="!point.peak" @click="selectPoint(point)">查看进程</button></td></tr></tbody></table></div><p v-if="!tablePoints.length" class="empty-state">此范围尚无记录。</p></section>
  </template>
</template>

<style scoped>
.history-controls{display:flex;align-items:end;gap:18px;flex-wrap:wrap}.history-field{display:flex;flex-direction:column;gap:10px;flex:1;min-width:180px}.history-field label{font-size:11px;color:var(--muted)}.history-field select{width:100%;font-size:11px}.history-ranges{display:flex;gap:4px;padding:4px;background:#111616;border:1px solid var(--border);border-radius:6px}.history-ranges button{padding:9px 12px;font-size:11px;border-radius:4px;color:var(--muted)}.history-ranges button.active{background:#303d35;color:#d8e3db}.history-controls>.text-button{padding:12px 0}.history-summary{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.history-summary .panel{display:flex;flex-direction:column;gap:12px}.history-summary span{font-size:11px;color:var(--muted)}.history-summary strong{font-size:25px;font-weight:400;color:var(--accent)}.history-summary small{font-size:10px;color:var(--muted);line-height:1.7}.history-chart .trend-chart{height:220px}.history-legend{display:flex;gap:14px;font-size:11px;white-space:nowrap}.history-legend span:first-child{color:var(--accent)}.history-legend span:last-child{color:#e8c58c}.history-table{max-height:440px}.history-table th{position:sticky;top:0;background:var(--panel)}.history-table td{white-space:nowrap}@media(max-width:700px){.history-summary{grid-template-columns:1fr;gap:0}.history-summary .panel{gap:9px}.history-controls{gap:12px;align-items:center}.history-field{flex-basis:100%;min-width:0}.history-ranges{flex:1;justify-content:space-between}.history-ranges button{font-size:10px;padding:8px 9px}.history-chart .trend-chart{height:180px}.history-chart .panel-heading{flex-direction:column}.history-controls>.text-button{font-size:10px}.history-summary strong{font-size:23px}}
</style>
