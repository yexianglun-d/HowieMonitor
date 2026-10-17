import type { MetricStatus } from '../types/monitor'

export const statusText: Record<MetricStatus, string> = { available: '可读取', notReady: '未就绪', permissionDenied: '权限不足', unsupported: '不支持', failed: '采集失败', driverMissing: '驱动缺失', unknown: '未获取' }
export function number(value: number | null | undefined, digits = 1) { return value == null ? '—' : value.toLocaleString('zh-CN', { maximumFractionDigits: digits }) }
export function capacity(value: number | null | undefined) { return value == null ? '—' : value >= 1024 ** 3 ? `${number(value / 1024 ** 3)} GiB` : `${number(value / 1024 ** 2)} MiB` }
export function rate(value: number | null | undefined) { return value == null ? '—' : value >= 1024 ** 2 ? `${number(value / 1024 ** 2)} MiB/s` : `${number(value / 1024)} KiB/s` }
export function uptime(seconds: number | undefined) { if (seconds == null) return '—'; const days = Math.floor(seconds / 86400); const hours = Math.floor(seconds % 86400 / 3600); const minutes = Math.floor(seconds % 3600 / 60); return `${days ? `${days} 天 ` : ''}${hours} 小时 ${minutes} 分钟` }
export function time(value: string | null | undefined) { return value ? new Date(value).toLocaleTimeString('zh-CN', { hour12: false }) : '—' }
