<script setup lang="ts">
import { computed } from 'vue'
import type { HistoryPoint } from '../../types/monitor'
const props = withDefaults(defineProps<{ points: HistoryPoint[]; color?: string; compact?: boolean; percent?: boolean; intervalSeconds?: number; showMaximum?: boolean }>(), { color: '#9adbc9', compact: false, percent: true, intervalSeconds: 2, showMaximum: false })
const width = 640, height = 160
const limits = computed(() => {
  const values = props.points.flatMap(point => [point.value, props.showMaximum ? point.maximum : null]).filter((value): value is number => value !== null && value !== undefined)
  const minimum = props.percent ? 0 : Math.min(0, ...values)
  return { minimum, maximum: props.percent ? 100 : Math.max(1, ...values) }
})
function position(point: HistoryPoint, value: number) {
  const first = Date.parse(props.points[0]!.sampledAt), duration = Math.max(1, Date.parse(props.points[props.points.length - 1]!.sampledAt) - first)
  return { x: (Date.parse(point.sampledAt) - first) / duration * width, y: height - (value - limits.value.minimum) / (limits.value.maximum - limits.value.minimum) * (height - 8) - 4 }
}
function paths(field: 'value' | 'maximum') {
  const points = props.points
  if (!points.length) return []
  const paths: string[] = []; let path = '', previousTime = 0
  for (const point of points) {
    const stamp = Date.parse(point.sampledAt), value = point[field]
    // 空值和长时间空缺断开曲线，不能连成连续的真实负载。
    if (value === null || value === undefined || previousTime && stamp - previousTime > Math.max(5000, props.intervalSeconds * 1500)) { if (path) paths.push(path); path = '' }
    if (value !== null && value !== undefined) { const { x, y } = position(point, value); path += `${path ? ' L' : 'M'}${x.toFixed(1)},${y.toFixed(1)}` }
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
</script>
<template>
  <div class="trend-chart" :class="{ compact }">
    <svg :viewBox="`0 0 ${width} ${height}`" preserveAspectRatio="none" role="img" :aria-label="`${intervalSeconds > 2 ? '历史汇总' : '真实采样'}曲线，共 ${points.length} 个时间点`">
      <path v-if="!compact" d="M0,4H640 M0,80H640 M0,156H640" class="chart-grid" />
      <path v-for="(segment, index) in segments" :key="index" :d="segment" fill="none" :stroke="color" stroke-width="2" vector-effect="non-scaling-stroke" />
      <path v-for="(segment, index) in maximumSegments" :key="`max-${index}`" :d="segment" fill="none" stroke="#e8c58c" stroke-dasharray="5 4" stroke-width="1.5" vector-effect="non-scaling-stroke" />
      <circle v-for="point in markers" :key="point.sampledAt" :cx="point.x" :cy="point.y" r="2.5" :fill="color" vector-effect="non-scaling-stroke" />
    </svg>
    <span v-if="segments.length === 0" class="chart-empty">等待有效采样</span>
  </div>
</template>
