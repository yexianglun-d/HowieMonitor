import { request, refreshCsrf } from './request'
import type { TerminalSession, TerminalStatus } from '../types/terminal'

export const getTerminalStatus = (computerId: string, signal: AbortSignal) => request<TerminalStatus>(`/api/terminal/status?${new URLSearchParams({ computerId })}`, { signal })
export async function createTerminalSession(computerId: string, password: string, columns: number, rows: number, signal: AbortSignal) {
  await refreshCsrf()
  return request<TerminalSession>('/api/terminal/sessions', { method: 'POST', body: JSON.stringify({ computerId, password, columns, rows }), signal })
}
export async function closeTerminalSession(computerId: string, sessionId: string) {
  await refreshCsrf()
  return request<void>(`/api/terminal/sessions/${encodeURIComponent(sessionId)}?${new URLSearchParams({ computerId })}`, { method: 'DELETE' })
}
