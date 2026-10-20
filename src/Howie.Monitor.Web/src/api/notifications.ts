import { request, refreshCsrf } from './request'
import type { AlertRule, ComputerNotificationView, NotificationUpdate, NotificationView } from '../types/notifications'

export const getNotifications = (signal: AbortSignal) => request<NotificationView>('/api/notifications', { signal })
export async function saveNotifications(value: NotificationUpdate, signal: AbortSignal) {
  await refreshCsrf()
  return request<NotificationView>('/api/notifications', { method: 'PUT', body: JSON.stringify(value), signal })
}
export async function testNotifications(signal: AbortSignal) {
  await refreshCsrf()
  return request<NotificationView>('/api/notifications/test', { method: 'POST', signal })
}
const computerUrl = (computerId: string) => '/api/notifications/computer?' + new URLSearchParams({ computerId })
export const getComputerNotifications = (computerId: string, signal: AbortSignal) => request<ComputerNotificationView>(computerUrl(computerId), { signal })
export async function saveComputerNotifications(computerId: string, rules: AlertRule[], signal: AbortSignal) {
  await refreshCsrf()
  return request<ComputerNotificationView>(computerUrl(computerId), { method: 'PUT', body: JSON.stringify({ rules }), signal })
}
