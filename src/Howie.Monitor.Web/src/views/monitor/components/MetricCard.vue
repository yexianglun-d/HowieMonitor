<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { gsap } from 'gsap'
import TrendChart from '../../../components/base/TrendChart.vue'
import { number, statusText } from '../../../utils/format'
import type { Metric, HistoryPoint } from '../../../types/monitor'
defineProps<{ title: string; subtitle: string; hardware?: string; metric: Metric | null; points: HistoryPoint[]; color: string; code: string }>()
defineEmits<{ select: [] }>()
const root = ref<HTMLElement | null>(null)
let media: gsap.MatchMedia | undefined
let lift: (value: number) => void = () => {}, press: (value: number) => void = () => {}
onMounted(() => {
  if (!root.value) return
  media = gsap.matchMedia()
  media.add('(prefers-reduced-motion: no-preference)', () => {
    press = gsap.quickTo(root.value, 'scale', { duration: .16, ease: 'power2.out' })
    return () => { press = () => {} }
  }, root.value)
  media.add('(hover: hover) and (prefers-reduced-motion: no-preference)', () => {
    lift = gsap.quickTo(root.value, 'y', { duration: .18, ease: 'power2.out' })
    return () => { lift = () => {} }
  }, root.value)
})
onUnmounted(() => media?.revert())
</script>
<template>
  <button ref="root" class="metric-card" :disabled="!metric" :style="{ '--metric-color': color }" @click="$emit('select')" @pointerenter="lift(-3)" @pointerleave="lift(0); press(1)" @pointerdown="press(.985)" @pointerup="press(1)" @pointercancel="press(1)">
    <div class="card-label"><span>{{ title }}</span><span class="card-code" aria-hidden="true">{{ code }}</span></div>
    <div class="metric-number">{{ metric?.status === 'available' ? number(metric.value) : '—' }}<small>%</small></div>
    <div v-if="hardware" class="card-hardware">{{ hardware }}</div>
    <div class="card-subtitle">{{ metric && metric.status !== 'available' ? statusText[metric.status] : subtitle }}</div>
    <TrendChart :points="points" :color="color" compact aria-hidden="true"/>
  </button>
</template>
