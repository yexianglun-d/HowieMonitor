import { request, refreshCsrf } from './request'
import type { NotificationUpdate, NotificationView } from '../types/notifications'

export const getNotifications = (signal: AbortSignal) => request<NotificationView>('/api/notifications', { signal })
export async function saveNotifications(value: NotificationUpdate, signal: AbortSignal) {
  await refreshCsrf()
  return request<NotificationView>('/api/notifications', { method: 'PUT', body: JSON.stringify(value), signal })
}
export async function testNotifications(signal: AbortSignal) {
  await refreshCsrf()
  return request<NotificationView>('/api/notifications/test', { method: 'POST', signal })
}
