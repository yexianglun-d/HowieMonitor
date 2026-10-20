<script setup lang="ts">
import { computed } from 'vue'
import { ElButton, ElTag } from 'element-plus'
import type { Metric } from '../../../types/monitor'
import { number, statusText } from '../../../utils/format'
import { temperatureAvailable, temperatureGroups, temperatureLabel, temperatureReason } from '../../../utils/temperatures'
const props = defineProps<{ metrics: Metric[]; dataStatus: string }>()
defineEmits<{ select: [metric: Metric]; details: [] }>()
const groups = computed(() => temperatureGroups(props.metrics))
</script>
<template>
  <section class="panel temperature-panel" aria-label="硬件温度">
    <div class="panel-heading"><div><h2>硬件温度</h2><p>{{ dataStatus === 'fresh' ? '当前采样 · 摄氏度' : dataStatus === 'waiting' ? '等待温度上报' : '最后采样温度 · 数据非最新' }}</p></div><ElButton type="primary" link @click="$emit('details')">查看温度指标</ElButton></div>
    <div class="temperature-grid">
      <article v-for="group in groups" :key="group.id" class="temperature-card">
        <div class="temperature-heading"><span>{{ group.title }}</span><ElTag v-if="!temperatureAvailable(group.primary)" disable-transitions type="warning" effect="plain" size="small">{{ group.primary ? statusText[group.primary.status] : '未上报' }}</ElTag></div>
        <p class="temperature-hardware">{{ group.hardware }}</p>
        <button v-if="temperatureAvailable(group.primary)" class="temperature-value" :aria-label="group.hardware + ' · ' + temperatureLabel(group.primary) + ' ' + number(group.primary.value) + ' 摄氏度，查看趋势'" @click="$emit('select', group.primary)">{{ number(group.primary.value) }}<small>°C</small></button>
        <strong v-else class="temperature-value unavailable">—<small>°C</small></strong>
        <p v-if="temperatureAvailable(group.primary)" class="temperature-caption">{{ temperatureLabel(group.primary) }}</p>
        <p v-else class="temperature-reason">{{ temperatureReason(group) }}</p>
        <details v-if="group.readings.length > 1" class="temperature-sensors"><summary>全部 {{ group.readings.length }} 项传感器</summary><div v-for="metric in group.readings" :key="metric.key" class="temperature-reading"><span>{{ temperatureLabel(metric) }}</span><button v-if="temperatureAvailable(metric)" @click="$emit('select', metric)">{{ number(metric.value) }} °C</button><span v-else>{{ statusText[metric.status] }}</span></div></details>
      </article>
    </div>
  </section>
</template>
<style scoped>
.temperature-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 12px }
.temperature-card { min-width: 0; border: 1px solid var(--border); border-radius: 8px; padding: 16px; background: #141b18 }
.temperature-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 6px; color: #dae5de; font-size: 13px }
.temperature-hardware { min-height: 40px; margin-top: 9px; color: var(--muted); font-size: 12px; line-height: 1.6; overflow-wrap: anywhere }
.temperature-value { display: block; padding: 0; margin: 12px 0 6px; color: var(--accent); font: 32px/1.4 var(--mono); font-variant-numeric: tabular-nums; text-align: left }
button.temperature-value:hover { text-decoration: underline; text-underline-offset: 5px; text-decoration-thickness: 1px }
.temperature-value small { margin-left: 6px; color: var(--muted); font-size: 14px }
.temperature-value.unavailable { color: var(--muted) }
.temperature-caption, .temperature-reason { font-size: 12px; line-height: 1.7; overflow-wrap: anywhere }
.temperature-caption { color: var(--muted) }
.temperature-reason { color: #e7cb9d }
.temperature-sensors { margin-top: 10px; font-size: 12px; color: var(--muted) }
.temperature-sensors summary { min-height: 32px; padding: 6px 0; cursor: pointer }
.temperature-reading { display: flex; align-items: center; justify-content: space-between; gap: 10px; border-top: 1px solid var(--border); padding: 5px 0; line-height: 1.7 }
.temperature-reading > span:first-child { min-width: 0; overflow-wrap: anywhere }
.temperature-reading button { flex-shrink: 0; color: var(--accent); min-height: 32px; padding: 4px 0 }
@media (max-width: 700px) {
  .temperature-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px }
  .temperature-card { padding: 13px }
  .temperature-value { font-size: 29px }
}
@media (max-width: 420px) { .temperature-grid { grid-template-columns: minmax(0, 1fr) } .temperature-hardware { min-height: 0 } }
</style>
