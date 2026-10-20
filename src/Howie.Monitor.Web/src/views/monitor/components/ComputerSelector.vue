<script setup lang="ts">
import { ref, watch, nextTick } from 'vue'
import { useHorizontalRail } from '../../../composables/useHorizontalRail'
import { ElButton } from 'element-plus'
import type { ComputerSummary } from '../../../types/monitor'
const props = defineProps<{ computers: ComputerSummary[]; selectedId: string }>()
defineEmits<{ select: [id: string]; enroll: [] }>()
const track = ref<HTMLElement | null>(null)
const { before, after, move, keydown } = useHorizontalRail(track, () => props.computers.map(item => item.deviceId).join('|'))
watch(() => props.selectedId, async () => {
  await nextTick()
  const rail = track.value, item = rail?.querySelector<HTMLElement>('[aria-pressed="true"]')
  if (rail && item) {
    const left = item.offsetLeft, right = left + item.offsetWidth
    if (left < rail.scrollLeft) rail.scrollLeft = left
    else if (right > rail.scrollLeft + rail.clientWidth) rail.scrollLeft = right - rail.clientWidth
  }
}, { immediate: true })
</script>
<template>
  <section class="computer-list" aria-label="选择监控电脑">
    <button v-if="before" class="computer-rail-arrow" aria-label="向左浏览电脑" @click="move(-1, $event.detail === 0)">‹</button><div ref="track" class="computer-choices" tabindex="0" aria-label="电脑切换，可左右滚动" @keydown="keydown"><button v-for="computer in computers" :key="computer.deviceId" class="computer-choice" :class="{ active: computer.deviceId === selectedId }" :aria-pressed="computer.deviceId === selectedId" :aria-label="computer.name + ' · ' + computer.deviceId + ' · ' + (computer.connectionStatus === 'online' ? '在线' : '停止上报')" @click="$emit('select', computer.deviceId)">
      <span class="connection-dot" :class="{ online: computer.connectionStatus === 'online' }"></span><strong>{{ computer.name }}</strong><span class="computer-state">{{ computer.connectionStatus === 'online' ? '在线' : '离线' }}</span><small>{{ computer.deviceId.slice(-8) }}</small>
    </button></div>
    <button v-if="after" class="computer-rail-arrow" aria-label="向右浏览电脑" @click="move(1, $event.detail === 0)">›</button>
    <ElButton v-if="!computers.length" @click="$emit('enroll')">添加第一台电脑</ElButton>
  </section>
</template>
