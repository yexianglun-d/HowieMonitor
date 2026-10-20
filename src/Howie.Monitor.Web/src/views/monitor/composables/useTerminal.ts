import { onMounted, onUnmounted, ref, watch } from 'vue'
import * as api from '../../../api/terminal'
import { useToast } from '../../../composables/useToast'
import type { TerminalFrame, TerminalPhase, TerminalStatus } from '../../../types/terminal'

export function useTerminal(computerId: () => string, write: (data: string, done: () => void) => void, reset: () => void) {
  const status = ref<TerminalStatus | null>(null), phase = ref<TerminalPhase>('idle'), account = ref(''), error = ref(''), statusError = ref(''), loading = ref(false)
  const toast = useToast()
  let generation = 0, disposed = false, socket: WebSocket | null = null
  let statusController: AbortController | null = null, connectController: AbortController | null = null
  let session: { computerId: string; id: string } | null = null, connectTimer: ReturnType<typeof setTimeout> | undefined
  const active = () => phase.value === 'connecting' || phase.value === 'ready'
  function release() {
    generation++; clearTimeout(connectTimer); connectController?.abort(); connectController = null
    const previous = session; session = null
    const previousSocket = socket; socket = null
    if (previousSocket) { previousSocket.onopen = previousSocket.onmessage = previousSocket.onclose = previousSocket.onerror = null; previousSocket.close() }
    // WebSocket 断开会立即关闭服务端会话；DELETE 同时清理尚未连接的短期授权。
    if (previous) void api.closeTerminalSession(previous.computerId, previous.id).catch(() => {})
    account.value = ''
  }
  function disconnect(message = '已结束会话') { release(); phase.value = 'closed'; error.value = message; void refresh() }
  async function refresh() {
    statusController?.abort(); const controller = new AbortController(); statusController = controller
    const current = generation, id = computerId()
    if (!id) { status.value = null; statusError.value = ''; loading.value = false; return }
    loading.value = !status.value
    try {
      const value = await api.getTerminalStatus(id, controller.signal)
      if (!disposed && !controller.signal.aborted && current === generation && id === computerId()) { status.value = value; statusError.value = '' }
    } catch (reason) {
      if (!disposed && !controller.signal.aborted && current === generation) { status.value = null; statusError.value = reason instanceof Error ? reason.message : '终端状态读取失败' }
    } finally { if (statusController === controller && current === generation && !disposed) loading.value = false }
  }
  async function connect(password: string, columns: number, rows: number) {
    if (active() || !computerId() || !status.value?.available || status.value.activeSessionId) return
    release(); reset(); phase.value = 'connecting'; error.value = ''
    const current = generation, id = computerId(), controller = new AbortController(); connectController = controller
    let opened: { sessionId: string } | null = null
    try {
      opened = await api.createTerminalSession(id, password, columns, rows, controller.signal)
      if (disposed || current !== generation || id !== computerId()) {
        void api.closeTerminalSession(id, opened.sessionId).catch(() => {}); return
      }
      session = { computerId: id, id: opened.sessionId }
      const url = new URL(`/api/terminal/sessions/${encodeURIComponent(opened.sessionId)}/socket`, window.location.href)
      url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'; url.search = new URLSearchParams({ computerId: id }).toString()
      const connection = new WebSocket(url); socket = connection
      const owns = () => !disposed && generation === current && socket === connection && computerId() === id
      function fail(message: string) {
        if (!owns()) return
        release(); phase.value = 'error'; error.value = message; toast.error(message)
      }
      connection.onmessage = event => {
        if (!owns()) return
        try {
          if (typeof event.data !== 'string' || event.data.length > 65536) throw new Error()
          const frame = JSON.parse(event.data) as TerminalFrame
          if (frame.type === 'ready' && phase.value === 'connecting' && frame.administrator && frame.account && frame.userSid) {
            clearTimeout(connectTimer); account.value = frame.account; phase.value = 'ready'
          } else if (frame.type === 'output' && phase.value === 'ready' && typeof frame.data === 'string' && frame.data.length <= 16384) {
            write(frame.data, () => { if (owns() && connection.readyState === WebSocket.OPEN) connection.send(JSON.stringify({ type: 'ack' })) })
          } else throw new Error()
        } catch { fail('终端响应不正确，会话已关闭') }
      }
      connection.onclose = event => {
        if (!owns()) return
        clearTimeout(connectTimer); socket = null; session = null; account.value = ''; phase.value = 'closed'
        error.value = event.reason || '终端连接已断开，请重新连接'
        void refresh()
      }
      connection.onerror = () => fail('终端连接失败，请检查目标电脑及网络')
      connectTimer = setTimeout(() => fail('建立终端连接超时'), 25000)
    } catch (reason) {
      if (disposed || controller.signal.aborted || current !== generation) return
      release(); phase.value = 'error'; error.value = reason instanceof Error ? reason.message : '终端连接失败'; toast.error(error.value)
    }
  }
  function input(value: string) {
    if (phase.value !== 'ready' || socket?.readyState !== WebSocket.OPEN) return false
    if (value.length > 65536 || socket.bufferedAmount + value.length * 6 > 262144) { toast.error('输入过长或连接繁忙，请分段发送'); return false }
    for (let start = 0; start < value.length;) {
      let end = Math.min(start + 4096, value.length)
      if (end < value.length && /[\uD800-\uDBFF]/.test(value[end - 1]!)) end--
      socket.send(JSON.stringify({ type: 'input', data: value.slice(start, end) })); start = end
    }
    return true
  }
  function resize(columns: number, rows: number) {
    if (phase.value === 'ready' && socket?.readyState === WebSocket.OPEN && columns >= 20 && columns <= 400 && rows >= 5 && rows <= 200)
      socket.send(JSON.stringify({ type: 'resize', columns, rows }))
  }
  watch(computerId, () => { release(); statusController?.abort(); phase.value = 'idle'; status.value = null; error.value = ''; statusError.value = ''; reset(); void refresh() }, { flush: 'sync' })
  let interval: ReturnType<typeof setInterval> | undefined
  onMounted(() => { void refresh(); interval = setInterval(() => { void refresh() }, 5000) })
  onUnmounted(() => { disposed = true; clearInterval(interval); statusController?.abort(); release() })
  return { status, phase, account, error, statusError, loading, connect, disconnect, input, resize, refresh }
}
