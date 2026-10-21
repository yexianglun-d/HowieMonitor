<script setup lang="ts">
import { onUnmounted, ref, watch } from 'vue'
import { ElButton, ElDialog, ElInput, ElTag } from 'element-plus'
import { createAgentUpdate, getAgentUpdateStatus } from '../../../api/agentUpdates'
import { useToast } from '../../../composables/useToast'
import type { AgentUpdateStatus } from '../../../types/agentUpdates'

const props = defineProps<{ target: { deviceId: string; name: string; agentVersion?: string | null } | null }>()
const emit = defineEmits<{ close: [] }>()
const status = ref<AgentUpdateStatus | null>(null), password = ref(''), error = ref(''), busy = ref(false), loading = ref(false)
const toast = useToast()
let timer: ReturnType<typeof setTimeout> | undefined, read: AbortController | null = null, write: AbortController | null = null, disposed = false
const phases: Record<string, string> = { queued: '等待电脑领取', downloading: '下载并校验安装包', installing: '安装中 · 采集服务会短暂重启', verifying: '等待新版本恢复上报', rollingBack: '正在恢复原版本', succeeded: '升级完成', rolledBack: '已回退至原版本', failed: '升级未执行', recoveryRequired: '需要在本机修复', repaired: '已在本机修复' }
function stop() { clearTimeout(timer); read?.abort(); read = null }
async function load(id: string) {
  read?.abort()
  const controller = new AbortController(); read = controller; loading.value = !status.value
  try {
    const next = await getAgentUpdateStatus(id, controller.signal)
    if (disposed || controller.signal.aborted || props.target?.deviceId !== id) return
    if (status.value && (status.value.release?.sha256 !== next.release?.sha256 || status.value.sessionId !== next.sessionId)) password.value = ''
    status.value = next; error.value = ''
  } catch (reason) {
    if (!disposed && !controller.signal.aborted && props.target?.deviceId === id) error.value = reason instanceof Error ? reason.message : '版本状态读取失败'
  } finally {
    if (!disposed && !controller.signal.aborted && props.target?.deviceId === id) {
      loading.value = false; timer = setTimeout(() => { void load(id) }, 5000)
    }
  }
}
watch(() => props.target?.deviceId, id => {
  stop(); status.value = null; password.value = ''; error.value = ''; loading.value = false
  if (id) void load(id)
}, { immediate: true })
async function submit() {
  const snapshot = status.value, target = props.target
  if (!target || !snapshot?.canUpdate || !snapshot.release || !password.value || busy.value || error.value) return
  const value = password.value; password.value = ''; busy.value = true; stop()
  const controller = new AbortController(); write = controller
  try {
    await createAgentUpdate(target.deviceId, value, snapshot.sessionId, snapshot.release.sha256, controller.signal)
    if (!disposed && !controller.signal.aborted) toast.success('升级任务已创建，可在这里查看进度')
  } catch (reason) {
    if (!disposed && !controller.signal.aborted) toast.error(reason instanceof Error ? reason.message : '未能确认升级任务，请刷新状态')
  } finally {
    if (!disposed && !controller.signal.aborted) { busy.value = false; if (props.target?.deviceId === target.deviceId) await load(target.deviceId) }
  }
}
onUnmounted(() => { disposed = true; stop(); write?.abort(); password.value = '' })
</script>
<template>
  <ElDialog :model-value="!!target" title="采集端版本与升级" width="min(560px, calc(100vw - 32px))" align-center :show-close="!busy" :close-on-click-modal="false" :close-on-press-escape="!busy" @update:model-value="value => { if (!value && !busy) emit('close') }">
    <form v-if="target" @submit.prevent="submit">
      <h3>{{ target.name }}</h3><p class="identity muted">{{ target.deviceId }}</p>
      <p v-if="loading" role="status">正在读取版本…</p>
      <dl v-if="status" class="version-grid">
        <dt>当前版本</dt><dd>{{ status.current?.version || target.agentVersion || '未知' }}<small>{{ status.current?.buildId || '旧版未上报构建标识' }}</small></dd>
        <dt>可用版本</dt><dd>{{ status.release?.version || '尚未发布' }}<small v-if="status.release">{{ status.release.buildId }}</small></dd>
      </dl>
      <div v-if="status?.operation" class="update-progress" aria-live="polite">
        <ElTag disable-transitions :type="status.operation.phase === 'succeeded' ? 'success' : ['failed', 'rolledBack', 'recoveryRequired'].includes(status.operation.phase) ? 'warning' : 'info'">{{ phases[status.operation.phase] || status.operation.phase }}</ElTag>
        <p v-if="status.operation.message">{{ status.operation.message }}</p>
        <small class="muted">任务目标 {{ status.operation.ticket.release.version }} · {{ new Date(status.operation.updatedAt).toLocaleString('zh-CN', { hour12: false }) }}</small>
      </div>
      <p v-if="status?.reason" class="muted">{{ status.reason }}</p>
      <template v-if="status?.canUpdate">
        <p>升级会短暂重启采集服务，保留电脑身份、历史、绑定和本机授权。新版本未恢复上报时，会尝试切回原版本。</p>
        <label for="agent-update-password">验证面板密码</label><ElInput id="agent-update-password" v-model="password" type="password" autocomplete="current-password" maxlength="128" :disabled="busy" placeholder="输入面板登录密码"/>
      </template>
      <p v-if="error" role="alert" class="error-message">{{ error }}</p>
      <footer><ElButton :disabled="busy" @click="emit('close')">关闭</ElButton><ElButton v-if="status?.canUpdate" type="primary" native-type="submit" :loading="busy" :disabled="!password || !!error">确认升级至 {{ status.release?.version }}</ElButton></footer>
    </form>
  </ElDialog>
</template>
<style scoped>
h3 { margin: 0; font-size: 17px }
p { margin: 12px 0; line-height: 1.7 }
.identity { font: 11px/1.7 var(--mono); overflow-wrap: anywhere; margin: 4px 0 20px }
.version-grid { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 16px; padding: 18px 0; border-block: 1px solid var(--border) }
dt { color: var(--muted) } dd { margin: 0; font-family: var(--mono) } dd small { display: block; color: var(--muted); overflow-wrap: anywhere; font-size: 11px; margin-top: 4px }
.update-progress { padding: 16px; margin-top: 18px; background: var(--el-fill-color-light); border-radius: 6px }
.update-progress p { margin-bottom: 8px } label { display: block; margin: 20px 0 8px }
footer { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 12px; margin-top: 24px }
footer :deep(.el-button + .el-button) { margin-left: 0 }
</style>
