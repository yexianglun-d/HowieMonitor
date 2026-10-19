<script setup lang="ts">
import type { ComputerSummary } from '../../../types/monitor'
defineProps<{ computers: ComputerSummary[]; selectedId: string }>()
defineEmits<{ select: [id: string]; enroll: [] }>()
</script>
<template>
  <section class="computer-list" aria-label="选择监控电脑">
    <div class="computer-choices"><button v-for="computer in computers" :key="computer.deviceId" class="computer-choice" :class="{ active: computer.deviceId === selectedId }" :aria-pressed="computer.deviceId === selectedId" @click="$emit('select', computer.deviceId)">
      <span class="connection-dot" :class="{ online: computer.connectionStatus === 'online' }"></span><strong>{{ computer.name }}</strong><span class="computer-state">{{ computer.connectionStatus === 'online' ? '在线' : '离线' }}</span><small>{{ computer.deviceId.slice(-8) }}</small>
    </button></div>
    <button v-if="!computers.length" class="secondary-button" @click="$emit('enroll')">添加第一台电脑</button>
  </section>
</template>
