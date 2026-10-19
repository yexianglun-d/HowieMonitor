import { computed, onActivated, onDeactivated, onMounted, onUnmounted, ref } from 'vue'
import * as api from '../../../api/notifications'
import type { NotificationView, NotificationSettings } from '../../../types/notifications'
import { useToast } from '../../../composables/useToast'

export function useNotifications() {
  const toast = useToast()
  const settings = ref<NotificationSettings>({ enabled: false, dailyLimit: 5, hasCredential: false, rules: [] })
  const events = ref<NotificationView['events']>([]), requestsToday = ref(0), budgetDate = ref('')
  const sendKey = ref(''), clearCredential = ref(false), loading = ref(true), ready = ref(false), busy = ref(false), error = ref('')
  let controller: AbortController | null = null, generation = 0, timer: ReturnType<typeof setTimeout> | undefined
  let active = true
  const saved = ref('')
  const dirty = computed(() => ready.value && (JSON.stringify(settings.value) !== saved.value || !!sendKey.value || clearCredential.value))
  function update(value: NotificationView, replaceSettings: boolean) {
    events.value = value.events; requestsToday.value = value.requestsToday; budgetDate.value = value.budgetDate
    if (replaceSettings) { settings.value = structuredClone(value.settings); saved.value = JSON.stringify(value.settings); ready.value = true }
  }
  async function load(replaceSettings = false, notifyFailure = false) {
    const current = generation
    const signal = controller?.signal
    if (!signal) return
    try { const value = await api.getNotifications(signal); if (current === generation) { update(value, replaceSettings || !ready.value); error.value = '' } }
    catch (err) {
      if (current === generation && !signal.aborted) {
        const message = err instanceof Error ? err.message : '无法读取通知设置。'
        if (active && (notifyFailure || !error.value)) toast.error(message)
        error.value = message
      }
    }
    finally { if (current === generation) { loading.value = false; clearTimeout(timer); if (active) timer = setTimeout(() => load(), 5000) } }
  }
  async function save() {
    if (!controller || busy.value || !ready.value) return
    const current = generation; busy.value = true
    try {
      const value = await api.saveNotifications({ enabled: settings.value.enabled, dailyLimit: settings.value.dailyLimit,
        sendKey: sendKey.value.trim() || null, clearCredential: clearCredential.value, rules: settings.value.rules.map(rule => ({ ...rule })) }, controller.signal)
      if (current !== generation) return
      update(value, true); sendKey.value = ''; clearCredential.value = false; error.value = ''; toast.success('通知设置已保存')
    } catch (err) { if (current === generation && !controller.signal.aborted) toast.error(err instanceof Error ? err.message : '设置保存失败，请重试。') }
    finally { if (current === generation) busy.value = false }
  }
  async function test() {
    if (!controller || busy.value) return
    const current = generation; busy.value = true
    try { const value = await api.testNotifications(controller.signal); if (current === generation) { update(value, false); error.value = ''; toast.info('测试通知已排队，请查看发送记录') } }
    catch (err) { if (current === generation && !controller.signal.aborted) toast.error(err instanceof Error ? err.message : '测试发送失败，请重试。') }
    finally { if (current === generation) busy.value = false }
  }
  function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value) { event.preventDefault(); event.returnValue = '' } }
  onMounted(() => { controller = new AbortController(); window.addEventListener('beforeunload', beforeUnload); void load(true) })
  // 全局设置的草稿随登录会话保留；离开页面仅停止轮询，不清空草稿或中断保存。
  onDeactivated(() => { active = false; clearTimeout(timer) })
  onActivated(() => { if (!active) { active = true; void load() } })
  onUnmounted(() => { generation++; active = false; clearTimeout(timer); controller?.abort(); sendKey.value = ''; window.removeEventListener('beforeunload', beforeUnload) })
  return { settings, events, requestsToday, budgetDate, sendKey, clearCredential, loading, ready, busy, dirty, error, save, test, refresh: () => load(!ready.value, true) }
}
