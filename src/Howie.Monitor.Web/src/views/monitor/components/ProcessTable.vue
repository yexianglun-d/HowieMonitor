<script setup lang="ts">
import { computed, ref } from 'vue'
import { capacity, number, statusText } from '../../../utils/format'
import type { ProcessReading } from '../../../types/monitor'
const props = withDefaults(defineProps<{ processes: ProcessReading[]; compact?: boolean }>(), { compact: false })
const query = ref(''), sort = ref<'cpu' | 'memory'>('cpu')
const rows = computed(() => {
  const matched = props.processes.filter(process => `${process.name} ${process.pid}`.toLowerCase().includes(query.value.toLowerCase()))
    .toSorted((a, b) => sort.value === 'cpu' ? (b.cpuPercent ?? -1) - (a.cpuPercent ?? -1) : (b.workingSetBytes ?? -1) - (a.workingSetBytes ?? -1))
  return props.compact ? matched.slice(0, 5) : matched
})
</script>
<template>
  <div class="process-tools"><input v-if="!compact" v-model="query" type="search" placeholder="搜索进程名称或 PID" aria-label="搜索进程"><select v-model="sort" aria-label="进程排序"><option value="cpu">按 CPU 占用</option><option value="memory">按内存占用</option></select></div>
  <div class="table-scroll"><table class="process-table"><thead><tr><th>进程名称</th><th>PID</th><th>CPU</th><th>内存</th><th v-if="!compact">状态</th></tr></thead><tbody>
    <tr v-for="process in rows" :key="`${process.pid}:${process.startedAt}`"><td><span class="process-icon">{{ process.name.slice(0, 1).toUpperCase() }}</span>{{ process.name }}</td><td>{{ process.pid }}</td><td>{{ process.cpuPercent === null ? '—' : `${number(process.cpuPercent)} %` }}</td><td>{{ capacity(process.workingSetBytes) }}</td><td v-if="!compact" :title="process.reason ?? ''">{{ statusText[process.status] }}</td></tr>
    <tr v-if="rows.length === 0"><td :colspan="compact ? 4 : 5" class="empty-state">{{ processes.length ? '没有匹配的进程' : '等待进程采样' }}</td></tr>
  </tbody></table></div>
  <p v-if="!compact" class="footnote">CPU 按全机逻辑处理器归一化；进程内存使用工作集，各进程之和不等于系统已用内存。</p>
</template>
