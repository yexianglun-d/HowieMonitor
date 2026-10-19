import { onActivated, onDeactivated, onUnmounted, ref } from 'vue'
import * as api from '../../../api/enrollment'
import type { EnrollmentCode, EnrollmentOverview, SetupDownload } from '../../../types/enrollment'
import { useToast } from '../../../composables/useToast'

export function useEnrollment() {
  const toast = useToast()
  const overview = ref<EnrollmentOverview | null>(null), download = ref<SetupDownload | null>(null), code = ref<EnrollmentCode | null>(null)
  const loading = ref(true), busy = ref(false), error = ref('')
  const controller = new AbortController(); let disposed = false, revision = 0, timer: ReturnType<typeof setTimeout> | undefined
  let active = true
  async function refresh(notifyFailure = false) {
    const current = revision
    try {
      const [state, packageInfo] = await Promise.all([api.getEnrollment(controller.signal), api.getSetupDownload(controller.signal)])
      if (disposed || current !== revision || busy.value) return
      if (overview.value?.currentDeviceId !== state.currentDeviceId) code.value = null
      if (code.value && code.value.expiresAt !== state.codeExpiresAt) code.value = null
      overview.value = state; download.value = packageInfo; error.value = ''
    } catch (err) {
      if (!disposed && current === revision && !busy.value) {
        const message = err instanceof Error ? err.message : '无法读取接入信息。'
        if (active && (notifyFailure || !error.value)) toast.error(message)
        error.value = message
      }
    }
    finally { if (!disposed) { loading.value = false; clearTimeout(timer); if (active) timer = setTimeout(() => refresh(), 5000) } }
  }
  async function generate() {
    if (busy.value || !overview.value) return
    revision++; busy.value = true
    try {
      const value = await api.createEnrollmentCode(controller.signal)
      if (!disposed) { code.value = value; overview.value.codeExpiresAt = value.expiresAt; toast.success('绑定码已生成') }
    } catch (err) { if (!disposed) toast.error(err instanceof Error ? err.message : '生成失败，请重试。') }
    finally { if (!disposed) { busy.value = false; void refresh() } }
  }
  async function revoke() {
    if (busy.value) return
    revision++; busy.value = true
    try { await api.revokeEnrollmentCode(controller.signal); if (!disposed) { code.value = null; toast.success('授权已撤销') } }
    catch (err) { if (!disposed) toast.error(err instanceof Error ? err.message : '撤销失败，请重试。') }
    finally { if (!disposed) { busy.value = false; void refresh() } }
  }
  void refresh()
  onDeactivated(() => { active = false; clearTimeout(timer) })
  onActivated(() => { if (!active) { active = true; void refresh() } })
  onUnmounted(() => { disposed = true; clearTimeout(timer); controller.abort(); code.value = null })
  return { overview, download, code, loading, busy, error, refresh: () => refresh(true), generate, revoke }
}
