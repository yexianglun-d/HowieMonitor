<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElButton, ElInput, ElTag } from 'element-plus'
import type { ComputerSummary } from '../../types/monitor'
import { capacity, number, uptime } from '../../utils/format'
import { temperatureAvailable, temperatureGroups, temperatureReason } from '../../utils/temperatures'

const props = defineProps<{ computers: ComputerSummary[] }>()
defineEmits<{ open: [id: string, page: 'overview' | 'notifications']; enroll: [] }>()
const query = ref(''), filter = ref('all')
const online = computed(() => props.computers.filter(computer => computer.connectionStatus === 'online').length)
const needsAttention = (computer: ComputerSummary) => computer.connectionStatus !== 'online' || computer.dataStatus !== 'fresh'
const attention = computed(() => props.computers.filter(needsAttention).length)
const filtered = computed(() => props.computers.filter(computer => (computer.name + ' ' + computer.deviceId).toLowerCase().includes(query.value.trim().toLowerCase())
  && (filter.value === 'all' || (filter.value === 'online' ? computer.connectionStatus === 'online' : needsAttention(computer)))))
const status = (computer: ComputerSummary) => ({ fresh: '数据最新', delayed: '更新延迟', stale: '数据过期', waiting: '等待采样', clockError: '时钟异常' })[computer.dataStatus] ?? '状态未知'
const percent = (value: number | null | undefined) => value == null ? '—' : number(value) + ' %'
const sampled = (value: string | null) => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '尚无采样'
const temperatures = (computer: ComputerSummary) => temperatureGroups(computer.temperatures ?? []).filter(group => ['cpu', 'gpu'].includes(group.category))
</script>
<template>
  <section class="fleet-tools" data-enter>
    <div class="fleet-filters" aria-label="筛选电脑">
      <button v-for="item in [{ key: 'all', label: '全部', count: computers.length }, { key: 'online', label: '在线', count: online }, { key: 'attention', label: '待检查', count: attention }]" :key="item.key" :aria-pressed="filter === item.key" :class="{ active: filter === item.key }" @click="filter = item.key">{{ item.label }} <span>{{ item.count }}</span></button>
    </div>
    <ElInput v-model="query" type="search" placeholder="搜索电脑名称或 ID" aria-label="搜索电脑" clearable/>
    <ElButton @click="$emit('enroll')">添加电脑</ElButton>
  </section>
  <div class="fleet-grid">
    <article v-for="computer in filtered" :key="computer.deviceId" class="panel fleet-card" :data-computer-id="computer.deviceId" data-enter>
      <header class="fleet-card-heading"><div><h2>{{ computer.name }}</h2><small class="fleet-id" :title="computer.deviceId">{{ computer.deviceId }}</small></div><ElTag disable-transitions :type="computer.connectionStatus === 'online' ? 'success' : 'info'" effect="plain">{{ computer.connectionStatus === 'online' ? '在线' : '停止上报' }}</ElTag></header>
      <p class="fleet-status" :class="{ attention: needsAttention(computer) }">{{ status(computer) }}<span v-if="computer.uptimeSeconds != null"> · 连续运行 {{ uptime(computer.uptimeSeconds) }}</span></p>
      <dl class="fleet-usage"><div><dt>CPU</dt><dd>{{ percent(computer.cpuPercent) }}</dd></div><div><dt>GPU</dt><dd>{{ percent(computer.gpuPercent) }}</dd></div><div><dt>内存</dt><dd>{{ percent(computer.memoryPercent) }}</dd></div></dl>
      <dl class="fleet-temperatures"><div v-for="group in temperatures(computer)" :key="group.id" :title="temperatureAvailable(group.primary) ? group.hardware : temperatureReason(group)"><dt>{{ group.title }}<small v-if="group.primary">{{ group.hardware }}</small></dt><dd>{{ temperatureAvailable(group.primary) ? number(group.primary.value) + ' °C' : '—' }}</dd></div></dl>
      <div class="fleet-space"><span>{{ computer.lowestSpaceVolume ? computer.lowestSpaceVolume + ' 可用空间' : '可用空间' }}</span><strong>{{ computer.lowestSpaceBytes == null ? '—' : capacity(computer.lowestSpaceBytes) }}</strong></div>
      <p class="muted fleet-sampled">{{ computer.sampledAt ? '最后采样 ' + sampled(computer.sampledAt) : '尚无采样' }}</p>
      <footer class="fleet-card-actions"><ElButton type="primary" @click="$emit('open', computer.deviceId, 'overview')">查看详情</ElButton><ElButton @click="$emit('open', computer.deviceId, 'notifications')">提醒规则</ElButton></footer>
    </article>
  </div>
  <section v-if="!filtered.length" class="panel empty-state"><p>{{ computers.length ? '没有匹配的电脑' : '尚未接入电脑' }}</p><ElButton v-if="!computers.length" type="primary" @click="$emit('enroll')">添加第一台电脑</ElButton></section>
</template>
<style scoped>
.fleet-tools { display: flex; flex-wrap: wrap; align-items: center; gap: 14px; margin-bottom: 22px }
.fleet-filters { display: flex; gap: 4px; flex: 1; min-width: 260px }
.fleet-filters button { min-height: 42px; padding: 8px 12px; border-radius: 7px; color: var(--muted); white-space: nowrap }
.fleet-filters button.active { background: #263c31; color: #d8eee2 }
.fleet-filters span { margin-left: 6px; font-family: var(--mono) }
.fleet-tools :deep(.el-input) { width: min(100%, 260px) }
.fleet-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: 20px }
.fleet-card { margin-bottom: 0; display: flex; flex-direction: column }
.fleet-card-heading { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px }
.fleet-card-heading > div { min-width: 0 }
.fleet-card h2 { overflow-wrap: anywhere }
.fleet-id { display: block; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); font: 11px/1.8 var(--mono); margin-top: 4px }
.fleet-status { font-size: 12px; color: var(--muted); margin-top: 14px; min-height: 20px }
.fleet-status.attention { color: #e4c88d }
.fleet-usage { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; padding: 18px 0; margin: 0 }
dt { color: var(--muted); font-size: 12px }
dd { margin: 5px 0 0; font-family: var(--mono); font-variant-numeric: tabular-nums }
.fleet-usage dd { font-size: 21px; white-space: nowrap }
.fleet-temperatures { margin: 0; border-top: 1px solid var(--border); flex: 1 }
.fleet-temperatures > div { display: flex; justify-content: space-between; align-items: center; gap: 16px; padding-top: 13px }
.fleet-temperatures dt { min-width: 0 }
.fleet-temperatures small { display: block; font-size: 11px; overflow-wrap: anywhere; margin-top: 2px }
.fleet-temperatures dd { flex-shrink: 0; margin: 0; color: var(--accent); font-size: 16px }
.fleet-space { display: flex; justify-content: space-between; gap: 14px; font-size: 12px; margin-top: 17px; padding-top: 14px; border-top: 1px solid var(--border) }
.fleet-space strong { font-family: var(--mono) }
.fleet-sampled { margin-top: 16px }
.fleet-card-actions { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 17px }
.fleet-card-actions :deep(.el-button + .el-button) { margin-left: 0 }
@media (max-width: 700px) { .fleet-grid { gap: 14px } .fleet-card { padding: 18px } .fleet-tools { gap: 10px } .fleet-tools :deep(.el-input) { width: 100%; order: 3 } .fleet-filters { min-width: 0 } .fleet-filters button { padding-inline: 9px } .fleet-usage dd { font-size: 20px } }
</style>
