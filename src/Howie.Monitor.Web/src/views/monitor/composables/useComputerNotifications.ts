import { computed, onActivated, onDeactivated, onMounted, onUnmounted, reactive, watch } from 'vue'
import * as api from '../../../api/notifications'
import type { AlertRule, AlertEvent, ComputerNotificationView } from '../../../types/notifications'
import { useToast } from '../../../composables/useToast'

interface Draft { rules: AlertRule[]; events: AlertEvent[]; saved: string; ready: boolean; loading: boolean; busy: boolean; error: string; saveError: string }
export function useComputerNotifications(computerId: () => string, removedIds: () => readonly string[] = () => []) {
  const drafts = reactive(new Map<string, Draft>()), toast = useToast(), writes = new Map<AbortController, string>()
  const draftFor = (id: string) => {
    if (!drafts.has(id)) drafts.set(id, reactive({ rules: [], events: [], saved: '[]', ready: false, loading: false, busy: false, error: '', saveError: '' }))
    return drafts.get(id)!
  }
  const current = computed(() => draftFor(computerId()))
  const changed = (draft: Draft) => draft.ready && JSON.stringify(draft.rules) !== draft.saved
  const dirty = computed(() => changed(current.value))
  let controller: AbortController | null = null, generation = 0, timer: ReturnType<typeof setTimeout> | undefined, active = true

  function apply(id: string, draft: Draft, value: ComputerNotificationView, replace: boolean) {
    if (value.computerId !== id || value.events.some(event => event.computerId !== id)) throw new Error('通知数据的电脑归属不一致，请重新读取。')
    draft.events = value.events
    if (replace) { draft.rules = structuredClone(value.rules); draft.saved = JSON.stringify(value.rules); draft.ready = true }
    draft.error = ''
  }
  async function refresh() {
    const id = computerId(), draft = draftFor(id), currentGeneration = generation
    if (!id || !active || removedIds().includes(id)) return
    controller?.abort(); const request = new AbortController(); controller = request
    draft.loading = !draft.ready
    try {
      const value = await api.getComputerNotifications(id, request.signal)
      if (currentGeneration === generation && !request.signal.aborted && !draft.busy) apply(id, draft, value, !changed(draft))
    } catch (err) {
      if (currentGeneration === generation && !request.signal.aborted) {
        const message = err instanceof Error ? err.message : '无法读取这台电脑的规则。'
        if (!draft.error) toast.error(message)
        draft.error = message
      }
    } finally {
      if (currentGeneration === generation) { draft.loading = false; clearTimeout(timer); if (active) timer = setTimeout(refresh, 5000) }
    }
  }
  async function save() {
    const id = computerId(), draft = draftFor(id)
    if (!id || !draft.ready || draft.busy || removedIds().includes(id)) return
    stop()
    const request = new AbortController(); writes.set(request, id); draft.busy = true; draft.saveError = ''
    // 保存捕获电脑及其草稿；中途切换只更新原电脑，不能覆盖当前电脑。
    try {
      const value = await api.saveComputerNotifications(id, draft.rules.map(rule => ({ ...rule })), request.signal)
      if (!request.signal.aborted) { apply(id, draft, value, true); draft.saveError = ''; if (computerId() === id && active) toast.success('这台电脑的提醒规则已保存') }
    } catch (err) { if (!request.signal.aborted) { draft.saveError = err instanceof Error ? err.message : '规则保存失败。'; if (computerId() === id && active) toast.error(draft.saveError) } }
    finally { draft.busy = false; writes.delete(request); if (active && computerId() === id && !request.signal.aborted) void refresh() }
  }
  function stop() { generation++; controller?.abort(); clearTimeout(timer) }
  function beforeUnload(event: BeforeUnloadEvent) { if ([...drafts.values()].some(changed)) { event.preventDefault(); event.returnValue = '' } }
  watch(computerId, () => { stop(); void refresh() }, { immediate: true, flush: 'sync' })
  watch(removedIds, ids => {
    for (const id of ids) {
      for (const [request, owner] of writes) if (owner === id) request.abort()
      drafts.delete(id)
    }
    if (ids.includes(computerId())) stop()
  }, { flush: 'sync' })
  onMounted(() => window.addEventListener('beforeunload', beforeUnload))
  onDeactivated(() => { active = false; stop() })
  onActivated(() => { if (!active) { active = true; void refresh() } })
  onUnmounted(() => { stop(); for (const request of writes.keys()) request.abort(); window.removeEventListener('beforeunload', beforeUnload) })
  return { current, dirty, refresh, save }
}
