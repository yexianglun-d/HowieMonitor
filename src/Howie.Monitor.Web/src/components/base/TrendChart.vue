<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { gsap } from 'gsap'
import type { HistoryPoint } from '../../types/monitor'
import { capacity, number, rate, time } from '../../utils/format'
const props = withDefaults(defineProps<{ points: HistoryPoint[]; color?: string; compact?: boolean; percent?: boolean; unit?: string; intervalSeconds?: number; showMaximum?: boolean; dateTime?: boolean }>(), { color: '#9adbc9', compact: false, percent: true, unit: '%', intervalSeconds: 2, showMaximum: false, dateTime: false })
const width = 640, height = 160
const root = ref<HTMLElement | null>(null), focused = ref<number | null>(null)
const limits = computed(() => {
  const values = props.points.flatMap(point => [point.value, props.showMaximum ? point.maximum : null]).filter((value): value is number => value != null)
  return { minimum: props.percent ? 0 : Math.min(0, ...values), maximum: props.percent ? 100 : Math.max(1, ...values) }
})
function position(point: HistoryPoint, value: number) {
  const first = Date.parse(props.points[0]!.sampledAt), duration = Math.max(1, Date.parse(props.points.at(-1)!.sampledAt) - first)
  return { x: (Date.parse(point.sampledAt) - first) / duration * width, y: height - (value - limits.value.minimum) / (limits.value.maximum - limits.value.minimum) * (height - 8) - 4 }
}
function paths(field: 'value' | 'maximum') {
  const paths: string[] = []; let path = '', previousTime = 0
  for (const point of props.points) {
    const stamp = Date.parse(point.sampledAt), value = point[field]
    // 空值和长时间空缺断开曲线，不能连成连续的真实负载。
    if (value == null || previousTime && stamp - previousTime > Math.max(5000, props.intervalSeconds * 1500)) { if (path) paths.push(path); path = '' }
    if (value != null) { const { x, y } = position(point, value); path += (path ? ' L' : 'M') + x.toFixed(1) + ',' + y.toFixed(1) }
    previousTime = stamp
  }
  if (path) paths.push(path)
  return paths
}
const segments = computed(() => paths('value'))
const maximumSegments = computed(() => props.showMaximum ? paths('maximum') : [])
const markers = computed(() => {
  const validCount = props.points.filter(point => point.value !== null).length
  return props.points.flatMap(point => point.value !== null && (validCount === 1 || point.coverageSeconds !== undefined && point.coverageSeconds < props.intervalSeconds) ? [{ ...position(point, point.value), sampledAt: point.sampledAt }] : [])
})
const selected = computed(() => focused.value === null ? null : props.points[Math.min(focused.value, props.points.length - 1)] ?? null)
const cursor = computed(() => selected.value ? position(selected.value, selected.value.value ?? limits.value.minimum) : null)
function valueText(value: number | null | undefined) {
  if (value == null) return '无有效数据'
  return props.unit === 'B' ? capacity(value) : props.unit === 'B/s' ? rate(value) : number(value) + ' ' + props.unit
}
function stampText(value: string | undefined) {
  return value && props.dateTime ? new Date(value).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }) : time(value)
}
function inspect(event: PointerEvent) {
  if (props.compact || !props.points.length) return
  const bounds = event.currentTarget instanceof Element ? event.currentTarget.getBoundingClientRect() : null
  if (!bounds) return
  const ratio = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width))
  const stamp = Date.parse(props.points[0]!.sampledAt) + ratio * (Date.parse(props.points.at(-1)!.sampledAt) - Date.parse(props.points[0]!.sampledAt))
  focused.value = props.points.reduce((best, point, index) => Math.abs(Date.parse(point.sampledAt) - stamp) < Math.abs(Date.parse(props.points[best]!.sampledAt) - stamp) ? index : best, 0)
}
function step(delta: number) { focused.value = Math.max(0, Math.min(props.points.length - 1, (focused.value ?? props.points.length - 1) + delta)) }
let media: gsap.MatchMedia | undefined
onMounted(() => {
  if (props.compact || !root.value) return
  media = gsap.matchMedia()
  media.add('(prefers-reduced-motion: no-preference)', () => {
    gsap.fromTo('.chart-lines', { opacity: .2 }, { opacity: 1, duration: .32, ease: 'power2.out', clearProps: 'opacity' })
  }, root.value)
})
onUnmounted(() => media?.revert())
</script>
<template>
  <div ref="root" class="trend-chart" :class="{ compact }">
    <div v-if="!compact" class="chart-axis" aria-hidden="true"><span>{{ valueText(limits.maximum) }}</span><span>{{ valueText((limits.maximum + limits.minimum) / 2) }}</span><span>{{ valueText(limits.minimum) }}</span></div>
    <div class="chart-surface" :tabindex="compact ? undefined : 0" :role="compact ? undefined : 'group'" :aria-label="compact ? undefined : '趋势图，使用左右方向键查看采样值'" @pointermove="inspect" @pointerdown="inspect" @pointerleave="focused = null" @blur="focused = null" @keydown.left.prevent="step(-1)" @keydown.right.prevent="step(1)">
      <svg :viewBox="'0 0 ' + width + ' ' + height" preserveAspectRatio="none" role="img" :aria-label="'采样曲线，共 ' + points.length + ' 个时间点'">
        <path v-if="!compact" d="M0,4H640 M0,80H640 M0,156H640" class="chart-grid" />
        <g class="chart-lines"><path v-for="(segment, index) in segments" :key="index" :d="segment" fill="none" :stroke="color" stroke-width="2" vector-effect="non-scaling-stroke" /><path v-for="(segment, index) in maximumSegments" :key="'max-' + index" :d="segment" fill="none" stroke="#e8c58c" stroke-dasharray="5 4" stroke-width="1.5" vector-effect="non-scaling-stroke" /><circle v-for="point in markers" :key="point.sampledAt" :cx="point.x" :cy="point.y" r="2.5" :fill="color" /></g>
        <template v-if="cursor && !compact"><path :d="'M' + cursor.x + ',0V160'" class="chart-cursor"/><circle v-if="selected?.value != null" :cx="cursor.x" :cy="cursor.y" r="4" :fill="color"/></template>
      </svg>
      <span v-if="segments.length === 0" class="chart-empty">暂无有效采样</span>
    </div>
    <div v-if="!compact" class="chart-readout" aria-live="polite"><span v-if="selected">{{ stampText(selected.sampledAt) }} <strong>{{ valueText(selected.value) }}</strong><template v-if="showMaximum"> · 峰值 {{ valueText(selected.maximum) }}</template></span><span v-else class="muted">悬停、轻触或按方向键查看数值</span></div>
    <div v-if="!compact" class="chart-time"><span>{{ stampText(points[0]?.sampledAt) }}</span><span>{{ stampText(points.at(-1)?.sampledAt) }}</span></div>
  </div>
</template>
