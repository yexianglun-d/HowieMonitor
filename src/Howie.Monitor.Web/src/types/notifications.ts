export interface AlertRule {
  id: string; name: string; enabled: boolean; kind: 'metric' | 'offline'; key: string; metricDeviceId: string
  unit: string; comparison: 'above' | 'below'; threshold: number; durationSeconds: number
  recoveryThreshold: number; recoverySeconds: number; notifyRecovery: boolean
}
export interface AlertEvent {
  id: string; ruleId: string | null; kind: string; title: string; description: string; createdAt: string
  status: string; detail: string | null; sentAt: string | null; computerId?: string | null
}
export interface NotificationSettings { enabled: boolean; dailyLimit: number; hasCredential: boolean; rules: AlertRule[] }
export interface NotificationView { settings: NotificationSettings; events: AlertEvent[]; requestsToday: number; budgetDate: string }
export interface NotificationUpdate { enabled: boolean; dailyLimit: number; sendKey: string | null; clearCredential: boolean; rules: AlertRule[] }
export interface ComputerNotificationView { computerId: string; rules: AlertRule[]; events: AlertEvent[] }
