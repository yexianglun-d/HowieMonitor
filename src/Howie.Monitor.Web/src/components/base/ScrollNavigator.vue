<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import type { ScrollSection } from '../../composables/useScrollExperience'
const props = defineProps<{ sections: ScrollSection[]; active: string }>()
defineEmits<{ go: [id: string, keyboard: boolean] }>()
const links = ref<HTMLElement | null>(null)
watch(() => props.active, async () => {
  await nextTick()
  const rail = links.value, item = rail?.querySelector<HTMLElement>('[aria-current="location"]')
  if (rail && item) {
    const left = item.offsetLeft, right = left + item.offsetWidth
    if (left < rail.scrollLeft) rail.scrollLeft = left
    else if (right > rail.scrollLeft + rail.clientWidth) rail.scrollLeft = right - rail.clientWidth
  }
})
</script>
<template>
  <div v-if="sections.length > 1" class="section-navigation" role="navigation" aria-label="页内导航">
    <div ref="links" class="section-links"><button v-for="(section, index) in sections" :key="section.id" :class="{ active: active === section.id }" :aria-current="active === section.id ? 'location' : undefined" @click="$emit('go', section.id, $event.detail === 0)"><span aria-hidden="true">{{ String(index + 1).padStart(2, '0') }}</span>{{ section.label }}</button></div>
    <button class="section-top" aria-label="返回首个段落" @click="$emit('go', sections[0]!.id, $event.detail === 0)">↑</button>
    <div class="reading-progress" aria-hidden="true"><span/></div>
  </div>
</template>
<style scoped>
.section-navigation { position: relative; display: flex; align-items: center; min-width: 0; gap: 12px }
.section-links { position: relative; display: flex; gap: 4px; min-width: 0; flex: 1; overflow-x: auto; scrollbar-width: none; scroll-snap-type: x proximity }
.section-links button { display: flex; align-items: center; gap: 8px; flex-shrink: 0; min-height: 40px; padding: 8px 12px; color: var(--muted); border-radius: 6px; scroll-snap-align: start; white-space: nowrap; transition: color var(--motion-fast) ease, background-color var(--motion-normal) ease }
.section-links button > span { font: 10px var(--mono); opacity: .6 }
.section-links button.active { color: #c8eee1; background: #263c32 }
.section-top { width: 36px; height: 36px; flex-shrink: 0; border: 1px solid var(--border); border-radius: 50%; color: var(--muted) }
.reading-progress { position: absolute; height: 2px; inset: auto 0 -9px; background: #29352f; overflow: hidden }
.reading-progress > span { display: block; height: 100%; background: var(--accent); transform: scaleX(0); transform-origin: left }
@media (hover: hover) and (pointer: fine) { button:hover { color: var(--accent); background: #22312a } }
@media (max-width: 700px) { .section-links button { padding-inline: 10px; font-size: 12px } .section-links button > span { display: none } }
@media (prefers-reduced-motion: reduce) { .section-links { scroll-snap-type: none } }
</style>
