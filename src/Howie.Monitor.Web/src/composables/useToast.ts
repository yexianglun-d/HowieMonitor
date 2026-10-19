import { readonly, ref } from 'vue'

export type ToastKind = 'success' | 'error' | 'warning' | 'info'
export interface ToastMessage { id: number; kind: ToastKind; message: string; count: number }
type PauseReason = 'hover' | 'focus'
interface ToastTimer { remaining: number; started: number; timeout?: ReturnType<typeof setTimeout>; pauses: Set<PauseReason> }

const messages = ref<ToastMessage[]>([])
const timers = new Map<number, ToastTimer>()
const durations: Record<ToastKind, number> = { success: 3000, info: 3000, warning: 4500, error: 6000 }
let sequence = 0

function dismiss(id: number) {
  clearTimeout(timers.get(id)?.timeout)
  timers.delete(id)
  messages.value = messages.value.filter(item => item.id !== id)
}
function start(id: number, timer: ToastTimer) {
  timer.started = Date.now()
  timer.timeout = setTimeout(() => dismiss(id), timer.remaining)
}
function show(kind: ToastKind, text: string) {
  const message = text.trim()
  if (!message) return
  const existing = messages.value.find(item => item.kind === kind && item.message === message)
  if (existing) {
    existing.count++
    const timer = timers.get(existing.id)!
    clearTimeout(timer.timeout); timer.remaining = durations[kind]
    if (!timer.pauses.size) start(existing.id, timer)
    return existing.id
  }
  if (messages.value.length >= 3) dismiss(messages.value[0]!.id)
  const id = ++sequence
  const timer: ToastTimer = { remaining: durations[kind], started: Date.now(), pauses: new Set() }
  messages.value.push({ id, kind, message, count: 1 })
  timers.set(id, timer); start(id, timer)
  return id
}
function pause(id: number, reason: PauseReason) {
  const timer = timers.get(id)
  if (!timer || timer.pauses.has(reason)) return
  if (!timer.pauses.size) { clearTimeout(timer.timeout); timer.remaining = Math.max(0, timer.remaining - (Date.now() - timer.started)) }
  timer.pauses.add(reason)
}
function resume(id: number, reason: PauseReason) {
  const timer = timers.get(id)
  if (!timer || !timer.pauses.delete(reason) || timer.pauses.size) return
  start(id, timer)
}
function clear() { for (const id of timers.keys()) dismiss(id) }

// 所有页面共用一个消息层，定时器集中清理，重复消息合并并重新计时。
const toast = {
  messages: readonly(messages), dismiss, pause, resume, clear,
  success: (message: string) => show('success', message),
  error: (message: string) => show('error', message),
  warning: (message: string) => show('warning', message),
  info: (message: string) => show('info', message),
}
export function useToast() { return toast }
