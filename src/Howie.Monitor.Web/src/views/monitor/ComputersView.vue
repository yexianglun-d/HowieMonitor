<script setup lang="ts">
import { useHorizontalRail } from '../../composables/useHorizontalRail'
import { computed, ref } from 'vue'
import { ElButton, ElInput, ElTag } from 'element-plus'
import type { ComputerSummary } from '../../types/monitor'
import { capacity, number, uptime } from '../../utils/format'
import { temperatureAvailable, temperatureGroups, temperatureReason } from '../../utils/temperatures'
import { gpuUsageAvailable, gpuUsageLabel } from '../../utils/gpus'
import AgentUpdateDialog from './components/AgentUpdateDialog.vue'

const props = defineProps<{ computers: ComputerSummary[] }>()
const updateTarget = ref<ComputerSummary | null>(null)
defineEmits<{ open: [id: string, page: 'overview' | 'notifications']; enroll: []; remove: [computer: { deviceId: string; name: string }] }>()
const query = ref(''), filter = ref('all'), layout = ref<'rail' | 'grid'>('rail'), rail = ref<HTMLElement | null>(null)
const online = computed(() => props.computers.filter(computer => computer.connectionStatus === 'online').length)
const needsAttention = (computer: ComputerSummary) => computer.connectionStatus !== 'online' || computer.dataStatus !== 'fresh'
const attention = computed(() => props.computers.filter(needsAttention).length)
const filtered = computed(() => props.computers.filter(computer => (computer.name + ' ' + computer.deviceId).toLowerCase().includes(query.value.trim().toLowerCase())
  && (filter.value === 'all' || (filter.value === 'online' ? computer.connectionStatus === 'online' : needsAttention(computer)))))
const { before, after, progress, move, keydown } = useHorizontalRail(rail, () => layout.value + ':' + filtered.value.map(item => item.deviceId).join('|'))
const status = (computer: ComputerSummary) => ({ fresh: '数据最新', delayed: '更新延迟', stale: '数据过期', waiting: '等待采样', clockError: '时钟异常' })[computer.dataStatus] ?? '状态未知'
const percent = (value: number | null | undefined) => value == null ? '—' : number(value) + ' %'
const sampled = (value: string | null) => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '尚无采样'
const temperatures = (computer: ComputerSummary) => temperatureGroups(computer.temperatures ?? []).filter(group => ['cpu', 'gpu'].includes(group.category))
</script>
<template>
  <section class="fleet-tools" data-enter>
    <div class="fleet-filters" aria-label="筛选电脑">
      <button v-for="item in [{ key: 'all', label: '全部', count: computers.length }, { key: 'online', label: '在线', count: online }, { key: 'attention', label: '待检查', count: attention }]" :key="item.key" :aria-pressed="filter === item.key" :class="{ active: filter === item.key }" @click="filter = item.key"><span>{{ item.count }}</span><strong>{{ item.label }}</strong></button>
    </div>
    <ElInput v-model="query" type="search" placeholder="搜索电脑名称或 ID" aria-label="搜索电脑" clearable/>
    <ElButton @click="$emit('enroll')">添加电脑</ElButton>
  </section>
  <div class="fleet-browse">
    <div><span class="fleet-caption">我的电脑</span><span class="muted">{{ filtered.length }} 台</span></div>
    <div class="fleet-browse-actions"><button class="layout-toggle" :aria-pressed="layout === 'grid'" @click="layout = layout === 'rail' ? 'grid' : 'rail'">{{ layout === 'rail' ? '▦ 网格查看' : '↔ 横向浏览' }}</button><div v-if="layout === 'rail' && filtered.length > 1" class="rail-arrows"><button aria-label="上一台电脑" :disabled="!before" @click="move(-1, $event.detail === 0)">←</button><button aria-label="下一台电脑" :disabled="!after" @click="move(1, $event.detail === 0)">→</button></div></div>
  </div>
  <div ref="rail" class="fleet-grid" :class="{ 'fleet-rail': layout === 'rail' }" :tabindex="layout === 'rail' ? 0 : undefined" :aria-label="layout === 'rail' ? '电脑卡片，可左右滚动或使用方向键' : '电脑卡片'" @keydown="keydown">
    <article v-for="computer in filtered" :key="computer.deviceId" class="panel fleet-card" :data-computer-id="computer.deviceId" data-enter>
      <header class="fleet-card-heading"><div><h2>{{ computer.name }}</h2><small class="fleet-id" :title="computer.deviceId">{{ computer.deviceId }}</small></div><ElTag disable-transitions :type="computer.connectionStatus === 'online' ? 'success' : 'info'" effect="plain">{{ computer.connectionStatus === 'online' ? '在线' : '停止上报' }}</ElTag></header>
      <p class="fleet-status" :class="{ attention: needsAttention(computer) }">{{ status(computer) }}<span v-if="computer.uptimeSeconds != null"> · 连续运行 {{ uptime(computer.uptimeSeconds) }}</span></p>
      <dl class="fleet-usage"><div><dt>CPU</dt><dd>{{ percent(computer.cpuPercent) }}</dd></div><div><dt>内存</dt><dd>{{ percent(computer.memoryPercent) }}</dd></div></dl>
      <dl class="fleet-gpus"><div v-for="(gpu, index) in computer.gpus ?? []" :key="gpu.deviceId"><dt>{{ gpu.name }}<small>GPU {{ index + 1 }} · {{ gpuUsageLabel(gpu.usage) }}</small></dt><dd>{{ gpuUsageAvailable(gpu.usage) ? percent(gpu.usage.value) : '—' }}</dd></div><div v-if="!computer.gpus?.length"><dt>GPU</dt><dd>—</dd></div></dl>
      <dl class="fleet-temperatures"><div v-for="group in temperatures(computer)" :key="group.id" :title="temperatureAvailable(group.primary) ? group.hardware : temperatureReason(group)"><dt>{{ group.title }}<small v-if="group.primary">{{ group.hardware }}</small></dt><dd>{{ temperatureAvailable(group.primary) ? number(group.primary.value) + ' °C' : '—' }}</dd></div></dl>
      <div class="fleet-space"><span>{{ computer.lowestSpaceVolume ? computer.lowestSpaceVolume + ' 可用空间' : '可用空间' }}</span><strong>{{ computer.lowestSpaceBytes == null ? '—' : capacity(computer.lowestSpaceBytes) }}</strong></div>
      <p class="muted fleet-sampled">{{ computer.sampledAt ? '最后采样 ' + sampled(computer.sampledAt) : '尚无采样' }}</p>
      <div class="fleet-version"><span :title="computer.build?.buildId || '旧版未提供构建标识'">采集端 {{ computer.agentVersion || '版本未知' }}<small v-if="computer.build"> · {{ computer.build.buildId.slice(0, 8) }}</small></span><ElButton link @click="updateTarget = computer">版本与升级</ElButton></div>
      <footer class="fleet-card-actions"><ElButton type="primary" @click="$emit('open', computer.deviceId, 'overview')">查看详情</ElButton><ElButton @click="$emit('open', computer.deviceId, 'notifications')">提醒规则</ElButton><ElButton type="danger" link class="fleet-remove" @click="$emit('remove', { deviceId: computer.deviceId, name: computer.name })">移除电脑</ElButton></footer>
    </article>
  </div>
  <div v-if="layout === 'rail' && filtered.length > 1" class="fleet-rail-footer"><span>左右浏览电脑</span><div class="rail-position" aria-hidden="true"><span :style="{ transform: `scaleX(${.12 + progress * .88})` }"/></div><span>{{ before ? '继续浏览' : '从这里开始' }} <span aria-hidden="true">→</span></span></div>
  <section v-if="!filtered.length" class="panel empty-state"><p>{{ computers.length ? '没有匹配的电脑' : '尚未接入电脑' }}</p><ElButton v-if="!computers.length" type="primary" @click="$emit('enroll')">添加第一台电脑</ElButton></section>
  <AgentUpdateDialog :target="updateTarget" @close="updateTarget = null"/>
</template>
<style scoped>
.fleet-tools { display: flex; flex-wrap: wrap; align-items: center; gap: 16px }
.fleet-filters { display: flex; gap: 8px; flex: 1; min-width: 260px }
.fleet-filters button { display: flex; flex-direction: column; align-items: flex-start; gap: 4px; min-width: 90px; min-height: 76px; padding: 10px 16px; border-radius: 7px; color: var(--muted); white-space: nowrap; transition: color var(--motion-fast) ease, background-color var(--motion-fast) ease }
.fleet-filters button.active { background: #263c31; color: #d8eee2 }
.fleet-filters span { font: 26px/1.2 var(--mono); color: #dbe8e0 }
.fleet-filters strong { font-size: 12px; font-weight: 400 }
.fleet-tools :deep(.el-input) { width: min(100%, 260px) }
.fleet-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: var(--section-gap) }
.fleet-grid.fleet-rail { display: flex; overflow-x: auto; scroll-snap-type: x proximity; overscroll-behavior-x: contain; scrollbar-width: thin; scrollbar-color: #40574c transparent; padding: 2px 1px 14px; gap: 20px }
.fleet-rail > .fleet-card { flex: 0 0 min(400px, 86%); scroll-snap-align: start }
.fleet-browse, .fleet-browse > div, .fleet-browse-actions, .rail-arrows, .fleet-rail-footer { display: flex; align-items: center; gap: 12px }
.fleet-browse { justify-content: space-between; margin-top: 8px }
.fleet-caption { font-size: 16px; font-weight: 550 }
.fleet-browse .muted, .layout-toggle { font-size: 12px }
.layout-toggle { min-height: 40px; color: var(--muted); padding: 6px 8px; border-radius: 6px }
.rail-arrows { gap: 8px }
.rail-arrows button { width: 38px; height: 38px; border: 1px solid var(--border); border-radius: 50%; font-size: 18px; transition: color var(--motion-fast) ease, border-color var(--motion-fast) ease, background-color var(--motion-fast) ease }
.rail-arrows button:disabled { opacity: .3; cursor: default }
.fleet-rail-footer { justify-content: space-between; color: var(--muted); font-size: 11px; margin-top: -10px }
.rail-position { height: 2px; flex: 1; max-width: 240px; background: var(--border); overflow: hidden }
.rail-position > span { display: block; height: 100%; background: var(--accent); transform-origin: left }
@media (hover: hover) and (pointer: fine) { .rail-arrows button:hover:not(:disabled), .layout-toggle:hover { background: #24372c; color: var(--accent); border-color: #608374 } .fleet-card:hover { border-color: #4c685a } }
.fleet-card { transition: border-color var(--motion-normal) ease; margin-bottom: 0; display: flex; flex-direction: column }
.fleet-card-heading { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px }
.fleet-card-heading > div { min-width: 0 }
.fleet-card h2 { overflow-wrap: anywhere }
.fleet-id { display: block; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); font: 11px/1.8 var(--mono); margin-top: 4px }
.fleet-status { font-size: 12px; color: var(--muted); margin-top: 14px; min-height: 20px }
.fleet-status.attention { color: #e4c88d }
.fleet-usage { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; padding: 18px 0; margin: 0 }
dt { color: var(--muted); font-size: 12px }
dd { margin: 5px 0 0; font-family: var(--mono); font-variant-numeric: tabular-nums }
.fleet-usage dd { font-size: 21px; white-space: nowrap }
.fleet-temperatures { margin: 0; border-top: 1px solid var(--border); flex: 1 }
.fleet-gpus { margin: 0 0 14px; border-top: 1px solid var(--border) }
.fleet-temperatures > div, .fleet-gpus > div { display: flex; justify-content: space-between; align-items: baseline; gap: 16px; padding-top: 13px }
.fleet-temperatures dt, .fleet-gpus dt { min-width: 0; overflow-wrap: anywhere }
.fleet-temperatures small, .fleet-gpus small { display: block; font-size: 11px; overflow-wrap: anywhere; margin-top: 2px }
.fleet-temperatures dd, .fleet-gpus dd { flex-shrink: 0; margin: 0; color: var(--accent); font-size: 16px }
.fleet-space { display: flex; justify-content: space-between; gap: 14px; font-size: 12px; margin-top: 17px; padding-top: 14px; border-top: 1px solid var(--border) }
.fleet-space strong { font-family: var(--mono) }
.fleet-sampled { margin-top: 16px }
.fleet-version { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: baseline; gap: 10px; color: var(--muted); font-size: 12px; margin-top: 8px }
.fleet-card-actions { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 17px }
.fleet-card-actions :deep(.el-button + .el-button) { margin-left: 0 }
.fleet-card-actions :deep(.fleet-remove.el-button) { margin-left: auto }
@media (max-width: 700px) { .fleet-tools { gap: 12px } .fleet-tools :deep(.el-input) { width: 100%; order: 3 } .fleet-filters { min-width: 0 } .fleet-filters button { min-width: 64px; padding-inline: 10px } .fleet-browse-actions { gap: 6px } .rail-arrows { gap: 4px } .fleet-rail-footer > span:last-child { display: none } .fleet-usage dd { font-size: 20px } }
@media (prefers-reduced-motion: reduce) { .fleet-grid.fleet-rail { scroll-snap-type: none } }
</style>
