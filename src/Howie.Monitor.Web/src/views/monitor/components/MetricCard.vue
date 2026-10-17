<script setup lang="ts">
import TrendChart from '../../../components/base/TrendChart.vue'
import { number, statusText } from '../../../utils/format'
import type { Metric, HistoryPoint } from '../../../types/monitor'
defineProps<{ title: string; subtitle: string; metric: Metric | null; points: HistoryPoint[]; color: string; code: string }>()
defineEmits<{ select: [] }>()
</script>
<template>
  <button class="metric-card" :style="{ '--metric-color': color }" @click="$emit('select')">
    <div class="card-label"><span>{{ title }}</span><span class="card-code">{{ code }} ↗</span></div>
    <div class="metric-number">{{ metric?.status === 'available' ? number(metric.value) : '—' }}<small>%</small></div>
    <div class="card-subtitle">{{ metric && metric.status !== 'available' ? statusText[metric.status] : subtitle }}</div>
    <TrendChart :points="points" :color="color" compact />
  </button>
</template>
