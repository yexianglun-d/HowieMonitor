<script setup lang="ts">
import { ElCollapse, ElCollapseItem, ElInput, ElSelect, ElOption } from 'element-plus'
import { computed, ref } from 'vue'
import { capacity, number, statusText } from '../../../utils/format'
import type { ProcessReading } from '../../../types/monitor'
const props = withDefaults(defineProps<{ processes: ProcessReading[]; compact?: boolean }>(), { compact: false })
const query = ref(''), sort = ref<'cpu' | 'memory'>('cpu')
const matched = computed(() => props.processes.filter(process => (process.name + ' ' + process.pid).toLowerCase().includes(query.value.toLowerCase()))
  .toSorted((a, b) => sort.value === 'cpu' ? (b.cpuPercent ?? -1) - (a.cpuPercent ?? -1) : (b.workingSetBytes ?? -1) - (a.workingSetBytes ?? -1)))
const rows = computed(() => props.compact ? matched.value.slice(0, 5) : matched.value)
</script>
<template>
  <div class="process-tools" :class="{ compact }"><ElInput v-if="!compact" v-model="query" type="search" placeholder="搜索名称或 PID" aria-label="搜索进程"/><span v-if="!compact" class="muted">{{ matched.length }} 项</span><ElSelect v-model="sort" aria-label="进程排序"><ElOption value="cpu" label="CPU 从高到低"/><ElOption value="memory" label="内存从高到低"/></ElSelect></div>
  <div class="table-scroll process-scroll"><table class="process-table"><thead><tr><th>进程</th><th class="process-pid">PID</th><th>CPU</th><th>内存</th><th v-if="!compact" class="process-state">状态</th></tr></thead><tbody>
    <tr v-for="process in rows" :key="process.pid + ':' + process.startedAt"><td><span class="process-name">{{ process.name }}</span><small class="mobile-process-meta">PID {{ process.pid }}</small><ElCollapse class="process-reason" v-if="!compact && (process.reason || process.status !== 'available')"><ElCollapseItem :title="statusText[process.status]" name="details"><p>{{ process.reason || '没有可用的进程读数。' }}</p></ElCollapseItem></ElCollapse></td><td class="process-pid">{{ process.pid }}</td><td>{{ process.cpuPercent === null ? '—' : number(process.cpuPercent) + ' %' }}</td><td>{{ capacity(process.workingSetBytes) }}</td><td v-if="!compact" class="process-state">{{ statusText[process.status] }}</td></tr>
    <tr v-if="rows.length === 0"><td :colspan="compact ? 4 : 5" class="empty-state">{{ processes.length ? '没有匹配的进程' : '尚无进程采样' }}</td></tr>
  </tbody></table></div>
  <ElCollapse class="help-details" v-if="!compact"><ElCollapseItem title="读数说明" name="details"><p>CPU 占用按整机计算。内存为进程工作集，各进程之和可能与系统已用内存不同。</p></ElCollapseItem></ElCollapse>
</template>
