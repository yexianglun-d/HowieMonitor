import { onUnmounted, ref, watch } from 'vue'
import * as api from '../../../api/notifications'
import type { NotificationView, NotificationSettings } from '../../../types/notifications'

export function useNotifications(deviceId: () => string | undefined) {
  const settings = ref<NotificationSettings>({ enabled: false, dailyLimit: 5, hasCredential: false, rules: [] })
  const events = ref<NotificationView['events']>([]), requestsToday = ref(0), budgetDate = ref('')
  const sendKey = ref(''), clearCredential = ref(false), loading = ref(true), ready = ref(false), busy = ref(false), error = ref(''), message = ref('')
  let controller: AbortController | null = null, generation = 0, timer: ReturnType<typeof setTimeout> | undefined
  function update(value: NotificationView, replaceSettings: boolean) {
    events.value = value.events; requestsToday.value = value.requestsToday; budgetDate.value = value.budgetDate
    if (replaceSettings) { settings.value = structuredClone(value.settings); ready.value = true }
  }
  async function load(replaceSettings = false) {
    const current = generation
    const signal = controller?.signal
    if (!signal) return
    try { const value = await api.getNotifications(signal); if (current === generation) { update(value, replaceSettings || !ready.value); error.value = '' } }
    catch (err) { if (current === generation && !signal.aborted) error.value = err instanceof Error ? err.message : '无法读取通知设置。' }
    finally { if (current === generation) { loading.value = false; clearTimeout(timer); timer = setTimeout(() => load(), 5000) } }
  }
  async function save() {
    if (!controller || busy.value || !ready.value) return
    const current = generation; busy.value = true; error.value = ''; message.value = ''
    try {
      const value = await api.saveNotifications({ enabled: settings.value.enabled, dailyLimit: settings.value.dailyLimit,
        sendKey: sendKey.value.trim() || null, clearCredential: clearCredential.value, rules: settings.value.rules.map(rule => ({ ...rule })) }, controller.signal)
      if (current !== generation) return
      update(value, true); sendKey.value = ''; clearCredential.value = false; message.value = '设置已保存，规则从现在重新观察。'
    } catch (err) { if (current === generation && !controller.signal.aborted) error.value = err instanceof Error ? err.message : '设置保存失败。' }
    finally { if (current === generation) busy.value = false }
  }
  async function test() {
    if (!controller || busy.value) return
    const current = generation; busy.value = true; error.value = ''; message.value = ''
    try { const value = await api.testNotifications(controller.signal); if (current === generation) { update(value, false); message.value = '测试已排队。请查看发送记录并在微信确认收件。' } }
    catch (err) { if (current === generation && !controller.signal.aborted) error.value = err instanceof Error ? err.message : '测试发送失败。' }
    finally { if (current === generation) busy.value = false }
  }
  watch(deviceId, () => {
    generation++; clearTimeout(timer); controller?.abort(); controller = new AbortController()
    sendKey.value = ''; clearCredential.value = false; loading.value = true; ready.value = false; busy.value = false; message.value = ''; void load(true)
  }, { immediate: true })
  onUnmounted(() => { generation++; clearTimeout(timer); controller?.abort(); sendKey.value = '' })
  return { settings, events, requestsToday, budgetDate, sendKey, clearCredential, loading, ready, busy, error, message, save, test, refresh: () => load(!ready.value) }
}
