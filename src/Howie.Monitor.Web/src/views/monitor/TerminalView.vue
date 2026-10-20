<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { ElAlert, ElButton, ElInput, ElTag } from 'element-plus'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { useTerminal } from './composables/useTerminal'
import { useToast } from '../../composables/useToast'

const props = defineProps<{ computerId: string; computerName: string }>()
defineEmits<{ enroll: [] }>()
const root = ref<HTMLElement | null>(null), password = ref(''), selected = ref(false)
let terminal: Terminal | null = null, fit: FitAddon | null = null, observer: ResizeObserver | null = null
const toast = useToast()
const { status, phase, account, error, statusError, loading, connect, disconnect, input, resize, refresh } = useTerminal(() => props.computerId,
  (data, done) => { if (terminal) terminal.write(data, done); else done() }, () => { password.value = ''; if (root.value) createTerminal() })
const labels = { idle: '未连接', connecting: '正在连接', ready: '管理员会话', closed: '已断开', error: '连接失败' }
function fitTerminal() { if (root.value?.clientWidth && root.value.clientHeight) { fit?.fit(); if (terminal) resize(terminal.cols, terminal.rows) } }
async function open() { const value = password.value; password.value = ''; fitTerminal(); await connect(value, Math.min(400, Math.max(20, terminal?.cols ?? 80)), Math.min(200, Math.max(5, terminal?.rows ?? 24))) }
async function paste() {
  try { const value = await navigator.clipboard.readText(); if (phase.value === 'ready') terminal?.paste(value) }
  catch { toast.error('无法读取剪贴板，请使用键盘粘贴') }
}
async function copy() {
  try { if (terminal?.hasSelection()) await navigator.clipboard.writeText(terminal.getSelection()) }
  catch { toast.error('复制失败，请手动选择文字复制') }
}
watch(phase, async value => {
  if (terminal) terminal.options.disableStdin = value !== 'ready'
  if (value === 'ready') { await nextTick(); fitTerminal(); terminal?.focus() }
})
function createTerminal() {
  observer?.disconnect(); terminal?.dispose(); selected.value = false
  terminal = new Terminal({ disableStdin: true, cursorBlink: true, cursorStyle: 'bar', scrollback: 3000, fontSize: 13,
    fontFamily: 'Consolas, "SFMono-Regular", monospace', screenReaderMode: true, allowProposedApi: false,
    theme: { background: '#101414', foreground: '#d6e5dc', cursor: '#9adbc9', selectionBackground: '#38594c' } })
  fit = new FitAddon(); terminal.loadAddon(fit); terminal.open(root.value!); fitTerminal()
  terminal.onData(input); terminal.onSelectionChange(() => { selected.value = terminal?.hasSelection() ?? false })
  observer = new ResizeObserver(fitTerminal); observer.observe(root.value!)
}
onMounted(createTerminal)
onUnmounted(() => { observer?.disconnect(); terminal?.dispose(); terminal = null })
</script>
<template>
  <section class="panel terminal-panel">
    <div class="panel-heading"><div><h2>管理员 PowerShell</h2><p>{{ computerName || '选择电脑' }} <span class="terminal-device-id">{{ computerId }}</span></p></div><ElTag disable-transitions :type="phase === 'ready' ? 'success' : phase === 'error' ? 'danger' : 'info'" effect="plain">{{ labels[phase] }}</ElTag></div>
    <div class="terminal-account"><span>执行账号</span><strong>{{ account || status?.account || '尚未确认' }}</strong><span v-if="phase === 'ready'" class="terminal-admin">Windows 管理员</span></div>
    <ElAlert v-if="!loading && !status?.available && phase !== 'ready'" :title="status?.reason || '接入电脑并开启管理员终端后即可连接。'" type="warning" show-icon :closable="false"/>
    <ElAlert v-if="status?.activeSessionId && phase !== 'connecting' && phase !== 'ready'" title="这台电脑已有终端会话，请在原页面结束后再连接。" type="info" :closable="false"/>
    <form v-if="phase !== 'connecting' && phase !== 'ready' && status?.available && !status.activeSessionId" class="terminal-connect" @submit.prevent="open">
      <label for="terminal-password">验证面板密码</label><ElInput id="terminal-password" v-model="password" type="password" autocomplete="current-password" maxlength="128" placeholder="输入面板登录密码" :disabled="!computerId"/><ElButton type="primary" native-type="submit" :disabled="!password || !computerId">连接管理员终端</ElButton>
    </form>
    <div class="terminal-toolbar"><div><ElButton v-if="phase === 'connecting' || phase === 'ready'" type="danger" plain @click="disconnect()">结束会话</ElButton><ElButton v-else :loading="loading" @click="refresh">刷新状态</ElButton><ElButton v-if="!status?.available" type="primary" link @click="$emit('enroll')">更新与本机授权</ElButton></div><div><ElButton :disabled="phase !== 'ready'" @click="paste">粘贴</ElButton><ElButton :disabled="!selected" @click="copy">复制选中</ElButton><ElButton @click="terminal?.clear()">清空显示</ElButton></div></div>
    <div class="terminal-surface"><div ref="root" class="terminal-screen" aria-label="目标电脑 PowerShell 终端"/><div v-if="phase !== 'ready'" class="terminal-placeholder">{{ phase === 'connecting' ? '正在等待目标电脑确认管理员身份…' : '连接后显示目标电脑的实时终端' }}</div></div>
    <p v-if="error || statusError" class="terminal-message" role="status">{{ error || statusError }}</p>
    <p class="footnote terminal-help">命令以管理员权限在这台电脑上执行。切换电脑或离开页面会结束会话；闲置 5 分钟或连接 1 小时后自动结束。授权账号注销后不可用。</p>
  </section>
</template>
<style scoped>
.terminal-panel { display: grid; gap: 16px }
.terminal-panel .panel-heading { margin: 0 }
.terminal-device-id { margin-left: 10px; font-family: var(--mono); overflow-wrap: anywhere }
.terminal-account { display: flex; align-items: baseline; flex-wrap: wrap; gap: 12px; font-size: 12px; color: var(--muted) }
.terminal-account strong { color: #d6e5dc; font-family: var(--mono); overflow-wrap: anywhere }
.terminal-admin { color: var(--accent) }
.terminal-connect { display: grid; grid-template-columns: auto minmax(180px, 320px) auto; align-items: center; justify-content: start; gap: 12px; font-size: 13px }
.terminal-toolbar, .terminal-toolbar > div { display: flex; align-items: center; flex-wrap: wrap; gap: 10px }
.terminal-toolbar { justify-content: space-between }
.terminal-surface { position: relative; min-width: 0; border: 1px solid var(--border); border-radius: 8px; background: #101414 }
.terminal-screen { height: clamp(360px, 58vh, 680px); padding: 12px; overflow: hidden }
.terminal-placeholder { position: absolute; inset: 0; display: grid; place-items: center; pointer-events: none; color: var(--muted); padding: 20px; text-align: center; font-size: 13px }
.terminal-message { color: #e4c88d; font-size: 12px; overflow-wrap: anywhere }
.terminal-help { max-width: 90ch }
@media (max-width: 700px) { .terminal-connect { grid-template-columns: 1fr; justify-content: stretch } .terminal-toolbar { align-items: flex-start } .terminal-screen { height: 420px; padding: 8px } .terminal-device-id { display: block; margin-left: 0; margin-top: 4px } }
</style>
