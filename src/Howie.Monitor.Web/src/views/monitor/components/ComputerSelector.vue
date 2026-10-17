<script setup lang="ts">
import type { ComputerSummary } from '../../../types/monitor'
import { number, time } from '../../../utils/format'

defineProps<{ computers: ComputerSummary[]; selectedId: string }>()
const emit = defineEmits<{ select: [id: string] }>()
</script>

<template>
  <section class="computer-list" aria-label="选择监控电脑">
    <button v-for="computer in computers" :key="computer.deviceId" class="computer-choice" :class="{ active: computer.deviceId === selectedId }" :aria-pressed="computer.deviceId === selectedId" @click="emit('select', computer.deviceId)">
      <span class="computer-choice-title"><strong>{{ computer.name }}</strong><span :class="{ online: computer.connectionStatus === 'online' }">{{ computer.connectionStatus === 'online' ? '在线' : '离线' }}</span></span>
      <span class="computer-choice-metrics">CPU {{ number(computer.cpuPercent) }} % · 内存 {{ number(computer.memoryPercent) }} %</span>
      <small>{{ computer.deviceId.slice(-8) }} · {{ computer.dataStatus === 'fresh' ? '数据最新' : computer.dataStatus === 'clockError' ? '时钟异常' : computer.dataStatus === 'waiting' ? '等待采样' : `最后采集 ${time(computer.sampledAt)}` }}</small>
    </button>
    <p v-if="!computers.length" class="panel empty-state">尚无已接入电脑，请从「电脑接入」添加。</p>
  </section>
</template>
