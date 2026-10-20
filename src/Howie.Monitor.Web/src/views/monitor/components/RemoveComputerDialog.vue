<script setup lang="ts">
import { onUnmounted, ref, watch } from 'vue'
import { ElButton, ElDialog, ElInput } from 'element-plus'
import { removeComputer } from '../../../api/monitor'
import { useToast } from '../../../composables/useToast'
const props = defineProps<{ target: { deviceId: string; name: string | null } | null }>()
const emit = defineEmits<{ close: []; removed: [id: string] }>()
const password = ref(''), busy = ref(false), error = ref(''), toast = useToast()
let controller: AbortController | null = null, disposed = false
watch(() => props.target, () => { password.value = ''; error.value = '' })
async function submit() {
  if (!props.target || !password.value || busy.value) return
  const id = props.target.deviceId, value = password.value
  password.value = ''; busy.value = true; error.value = ''
  const request = new AbortController(); controller = request
  try {
    await removeComputer(id, value, request.signal)
    if (disposed || request.signal.aborted) return
    emit('removed', id); emit('close'); toast.success('电脑及其数据已移除')
  } catch (reason) {
    if (!disposed && !request.signal.aborted) { error.value = reason instanceof Error ? reason.message : '移除失败，请重试'; toast.error(error.value) }
  } finally { if (!disposed) busy.value = false }
}
onUnmounted(() => { disposed = true; controller?.abort(); password.value = '' })
</script>
<template>
  <ElDialog :model-value="!!target" title="彻底移除电脑" class="remove-computer-dialog" width="min(480px, calc(100vw - 32px))" align-center :show-close="!busy" :close-on-click-modal="false" :close-on-press-escape="!busy" @update:model-value="value => { if (!value && !busy) emit('close') }">
    <form v-if="target" @submit.prevent="submit">
      <dl class="remove-identity"><dt>电脑</dt><dd>{{ target.name || '未记录名称' }}</dd><dt>ID</dt><dd>{{ target.deviceId }}</dd></dl>
      <p>删除全部监控数据和通知配置，撤销接入凭据，并结束终端会话。此操作无法撤销。</p>
      <p class="muted">已有备份和电脑本地数据不会随之删除。重新接入需要新绑定码。</p>
      <label for="remove-computer-password">验证面板密码</label><ElInput id="remove-computer-password" v-model="password" type="password" autocomplete="current-password" maxlength="128" :disabled="busy" placeholder="输入面板登录密码"/>
      <p v-if="error" role="alert" class="error-message">{{ error }}</p>
      <div class="remove-actions"><ElButton :disabled="busy" @click="emit('close')">取消</ElButton><ElButton type="danger" native-type="submit" :disabled="!password" :loading="busy">{{ busy ? '正在移除…' : '确认移除' }}</ElButton></div>
    </form>
  </ElDialog>
</template>
<style scoped>
.remove-identity { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 8px 16px; margin: 0 0 20px }
.remove-identity dt { color: var(--muted) }
.remove-identity dd { margin: 0; overflow-wrap: anywhere; font-family: var(--mono); color: var(--el-text-color-primary) }
p { line-height: 1.7; margin: 12px 0 }
label { display: block; margin: 24px 0 8px }
.remove-actions { display: flex; justify-content: flex-end; gap: 12px; margin-top: 24px }
</style>
