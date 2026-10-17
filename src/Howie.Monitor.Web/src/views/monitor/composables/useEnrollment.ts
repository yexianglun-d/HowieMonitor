import { onUnmounted, ref } from 'vue'
import * as api from '../../../api/enrollment'
import type { EnrollmentCode, EnrollmentOverview, SetupDownload } from '../../../types/enrollment'

export function useEnrollment() {
  const overview = ref<EnrollmentOverview | null>(null), download = ref<SetupDownload | null>(null), code = ref<EnrollmentCode | null>(null)
  const loading = ref(true), busy = ref(false), confirmed = ref(false), error = ref(''), message = ref('')
  const controller = new AbortController(); let disposed = false, revision = 0, timer: ReturnType<typeof setTimeout> | undefined
  async function refresh() {
    const current = revision
    try {
      const [state, packageInfo] = await Promise.all([api.getEnrollment(controller.signal), api.getSetupDownload(controller.signal)])
      if (disposed || current !== revision || busy.value) return
      if (overview.value?.currentDeviceId !== state.currentDeviceId) { confirmed.value = false; code.value = null }
      if (code.value && code.value.expiresAt !== state.codeExpiresAt) code.value = null
      overview.value = state; download.value = packageInfo; error.value = ''
    } catch (err) { if (!disposed && current === revision && !busy.value) error.value = err instanceof Error ? err.message : '无法读取接入信息。' }
    finally { if (!disposed) { loading.value = false; clearTimeout(timer); timer = setTimeout(refresh, 5000) } }
  }
  async function generate() {
    if (busy.value || !overview.value) return
    revision++; busy.value = true; error.value = ''; message.value = ''
    try {
      const value = await api.createEnrollmentCode(overview.value.currentDeviceId, confirmed.value, controller.signal)
      if (!disposed) { code.value = value; overview.value.codeExpiresAt = value.expiresAt; message.value = '绑定码已生成，请在被监控电脑的向导中输入。' }
    } catch (err) { if (!disposed) error.value = err instanceof Error ? err.message : '生成失败。' }
    finally { if (!disposed) { busy.value = false; void refresh() } }
  }
  async function revoke() {
    if (busy.value) return
    revision++; busy.value = true
    try { await api.revokeEnrollmentCode(controller.signal); if (!disposed) { code.value = null; message.value = '本次接入授权已撤销，当前电脑继续运行。' } }
    catch (err) { if (!disposed) error.value = err instanceof Error ? err.message : '撤销失败。' }
    finally { if (!disposed) { busy.value = false; void refresh() } }
  }
  void refresh()
  onUnmounted(() => { disposed = true; clearTimeout(timer); controller.abort(); code.value = null })
  return { overview, download, code, loading, busy, confirmed, error, message, refresh, generate, revoke }
}
