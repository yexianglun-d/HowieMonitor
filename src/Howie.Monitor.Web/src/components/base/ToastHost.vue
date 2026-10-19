<script setup lang="ts">
import { onUnmounted } from 'vue'
import { useToast } from '../../composables/useToast'
const toast = useToast()
const { messages } = toast
const labels = { success: '成功', error: '错误', warning: '警告', info: '提示' }
function leaveFocus(id: number, event: FocusEvent) {
  if (!(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null)) toast.resume(id, 'focus')
}
onUnmounted(toast.clear)
</script>
<template>
  <Teleport to="body">
    <TransitionGroup name="toast" tag="div" class="toast-host" aria-label="消息提示">
      <div v-for="item in messages" :key="item.id" class="toast-message" :class="'toast-' + item.kind" :role="item.kind === 'error' ? 'alert' : 'status'" aria-atomic="true" @mouseenter="toast.pause(item.id, 'hover')" @mouseleave="toast.resume(item.id, 'hover')" @focusin="toast.pause(item.id, 'focus')" @focusout="leaveFocus(item.id, $event)" @keydown.esc.stop="toast.dismiss(item.id)">
        <svg class="toast-icon" viewBox="0 0 20 20" aria-hidden="true"><circle cx="10" cy="10" r="8"/><path v-if="item.kind === 'success'" d="m6 10 3 3 5-6"/><path v-else-if="item.kind === 'error'" d="m7 7 6 6m0-6-6 6"/><path v-else-if="item.kind === 'warning'" d="M10 5.5v5m0 3v.5"/><path v-else d="M10 9v5m0-8v.5"/></svg>
        <span class="sr-only">{{ labels[item.kind] }}：</span><p class="toast-text">{{ item.message }}</p><span v-if="item.count > 1" class="toast-count" :aria-label="'重复 ' + item.count + ' 次'">{{ item.count }}</span><button type="button" class="toast-close" aria-label="关闭消息" @click="toast.dismiss(item.id)"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4 4 8 8m0-8-8 8"/></svg></button>
      </div>
    </TransitionGroup>
  </Teleport>
</template>
<style scoped>
.toast-host { position: fixed; top: calc(22px + env(safe-area-inset-top, 0px)); left: 50%; transform: translateX(-50%); z-index: 1200; width: min(440px, calc(100vw - 32px)); display: flex; flex-direction: column; gap: 10px; pointer-events: none }
.toast-message { display: flex; align-items: center; gap: 10px; width: 100%; min-height: 56px; padding: 10px 12px 10px 16px; border: 1px solid; border-radius: 8px; box-shadow: 0 8px 28px #0005; pointer-events: auto }
.toast-success { color: #b8e9cc; background: #203c2b; border-color: #4a7759 }
.toast-error { color: #f3c0b7; background: #432b27; border-color: #916058 }
.toast-warning { color: #efd6a5; background: #3b3221; border-color: #856e42 }
.toast-info { color: #c6dced; background: #263744; border-color: #58778d }
.toast-icon { width: 20px; height: 20px; flex-shrink: 0; fill: none; stroke: currentColor; stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round }
.toast-text { flex: 1; min-width: 0; color: inherit; font-size: 14px; line-height: 1.6; overflow-wrap: anywhere }
.toast-count { font-size: 12px; border: 1px solid currentColor; border-radius: 12px; padding: 0 6px; min-width: 22px; text-align: center; line-height: 20px }
.toast-close { display: grid; place-items: center; flex-shrink: 0; width: 32px; height: 32px; padding: 0; border-radius: 5px; color: inherit }
.toast-close:hover { background: #ffffff0d }
.toast-close svg { width: 14px; height: 14px; fill: none; stroke: currentColor; stroke-width: 1.6; stroke-linecap: round }
.toast-enter-active, .toast-leave-active { transition: opacity 180ms ease, transform 180ms ease }
.toast-enter-from, .toast-leave-to { opacity: 0; transform: translateY(-12px) scale(.98) }
.toast-move { transition: transform 180ms ease }
.toast-leave-active { position: absolute; left: 0; right: 0 }
@media (max-width: 700px) {
  .toast-host { top: calc(12px + env(safe-area-inset-top, 0px)) }
  .toast-message { min-height: 60px; padding: 6px 6px 6px 14px; gap: 8px }
  .toast-close { width: 44px; height: 44px }
  .toast-text { font-size: 13px }
}
@media (prefers-reduced-motion: reduce) {
  .toast-enter-active, .toast-leave-active, .toast-move { transition: none }
  .toast-enter-from, .toast-leave-to { transform: none }
}
</style>
