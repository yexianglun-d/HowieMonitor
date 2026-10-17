import { request, refreshCsrf } from './request'
import type { AuthSession, Device, Latest, HistoryPoint, HistoryMetric, HistoryComputer, HistorySeries, HistoricalProcesses, SampleIdentity } from '../types/monitor'

export const getSession = () => request<AuthSession>('/api/auth/session')
export const getDevice = (signal: AbortSignal) => request<Device | null>('/api/device', { signal })
export const getLatest = (signal: AbortSignal) => request<Latest>('/api/metrics/latest', { signal })
export const getHistory = (key: string, deviceId: string, unit: string, signal: AbortSignal) => request<HistoryPoint[]>(`/api/history?${new URLSearchParams({ key, deviceId, unit, minutes: '10' })}`, { signal })
export const getHistoryComputers = (signal: AbortSignal) => request<HistoryComputer[]>('/api/history/computers', { signal })
export const getHistoryMetrics = (computerId: string, signal: AbortSignal) => request<HistoryMetric[]>(`/api/history/metrics?${new URLSearchParams({ computerId })}`, { signal })
export const getHistorySeries = (computerId: string, metric: HistoryMetric, hours: number, signal: AbortSignal) =>
  request<HistorySeries>(`/api/history/series?${new URLSearchParams({ computerId, key: metric.key, deviceId: metric.deviceId, unit: metric.unit, hours: String(hours) })}`, { signal })
export const getHistoryProcesses = (computerId: string, identity: SampleIdentity, signal: AbortSignal) =>
  request<HistoricalProcesses>(`/api/history/processes?${new URLSearchParams({ computerId, sessionId: identity.sessionId, sequence: String(identity.sequence) })}`, { signal })
export async function login(password: string, setup: boolean) {
  await refreshCsrf()
  await request(setup ? '/api/auth/setup' : '/api/auth/login', { method: 'POST', body: JSON.stringify({ password }) })
  await refreshCsrf()
}
export async function logout() { await refreshCsrf(); await request('/api/auth/logout', { method: 'POST' }); await refreshCsrf() }
