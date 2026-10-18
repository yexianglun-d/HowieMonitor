<script setup lang="ts">
import { computed, ref } from 'vue'
import type { HistoryMetric, Metric } from '../../types/monitor'
import type { AlertRule } from '../../types/notifications'
import { useNotifications } from './composables/useNotifications'

const props = defineProps<{ deviceId?: string; metrics: Metric[]; label: (metric: HistoryMetric) => string }>()
const { settings, events, requestsToday, budgetDate, sendKey, clearCredential, loading, ready, busy, error, message, save, test, refresh } = useNotifications(() => props.deviceId)
const choice = ref('__offline')
const metricId = (metric: HistoryMetric) => JSON.stringify([metric.key, metric.deviceId, metric.unit])
const choices = computed(() => props.metrics.filter(metric => !metric.deviceId.startsWith('cpu/logical/')))
function addRule() {
  if (settings.value.rules.length >= 20) return
  const metric = choices.value.find(item => metricId(item) === choice.value)
  if (!metric && choice.value !== '__offline') return
  const below = metric?.key === 'volume.free'
  const threshold = metric?.unit === '%' ? 90 : metric?.unit === 'B' ? 10 * 1024 ** 3 : 80
  const rule: AlertRule = { id: crypto.randomUUID(), name: metric ? props.label(metric).slice(0, 64) : '停止上报', enabled: true,
    kind: metric ? 'metric' : 'offline', key: metric?.key ?? '', metricDeviceId: metric?.deviceId ?? '', unit: metric?.unit ?? '',
    comparison: below ? 'below' : 'above', threshold, durationSeconds: metric ? 60 : 300,
    recoveryThreshold: threshold * (below ? 1.1 : .9), recoverySeconds: 30, notifyRecovery: true }
  settings.value.rules.push(rule)
}
const statusText: Record<string, string> = { queued: '等待发送', sending: '发送中', accepted: '服务已接受', failed: '发送失败',
  unknown: '结果未知', cancelled: '已取消', expired: '已过期', limited: '额度限制' }
const date = (value: string) => new Date(value).toLocaleString('zh-CN', { hour12: false })
</script>

<template>
  <p class="state-banner">通知设置应用于所有接入电脑，触发与恢复分别判断，发送预算共用。硬件专属指标仅在对应指标可读的电脑上生效。</p>
  <p v-if="loading" class="panel empty-state" role="status">正在读取通知设置…</p>
  <section v-else-if="!ready" class="panel notification-panel"><p class="error-message" role="alert">{{ error || '通知设置尚未读取，请重试。' }}</p><button class="text-button" @click="refresh">重新读取 ↻</button></section>
  <template v-else>
    <p v-if="error" class="error-banner" role="alert">{{ error }}</p>
    <p v-if="message" class="state-banner" role="status">{{ message }}</p>
    <form class="notification-form" @submit.prevent="save">
      <section class="panel notification-panel">
        <div class="panel-heading"><div><h2>微信通知</h2><p>Server酱 Turbo · 保存后生效</p></div><label class="check-label"><input v-model="settings.enabled" type="checkbox">通知总开关</label></div>
        <p class="footnote">开启后，连续满足规则才发送一次；恢复提醒单独控制。缺失或过期数据不作为恢复。</p>
        <div class="notification-fields">
          <label>SendKey<input v-model="sendKey" type="password" autocomplete="new-password" maxlength="203" placeholder="SCT 开头的 SendKey" :disabled="clearCredential"><small>{{ settings.hasCredential ? '已配置，留空保留' : '尚未配置' }}</small></label>
          <label>每日请求预算<input v-model.number="settings.dailyLimit" type="number" min="1" max="1000" required><small>北京时间 {{ budgetDate }} · 已请求 {{ requestsToday }} 次（测试与失败也计入）</small></label>
        </div>
        <div class="notification-actions"><a href="https://sct.ftqq.com/" target="_blank" rel="noopener noreferrer">获取 SendKey ↗</a><label class="check-label"><input v-model="clearCredential" type="checkbox" @change="sendKey = ''">清除已存密钥</label><button type="button" class="text-button" :disabled="busy || !settings.hasCredential || !!sendKey || clearCredential" @click="test">发送测试通知 ↗</button></div>
        <p class="footnote">密钥加密保存，不会回显。只向 Server酱发送电脑名称、提醒条件、时间和面板入口。测试通知使用已保存密钥，总开关关闭时也可主动测试。服务已接受后仍需在微信确认收件。</p>
      </section>
      <section class="panel notification-panel">
        <div class="panel-heading"><div><h2>提醒规则 <span class="small-count">{{ settings.rules.length }} / 20</span></h2><p>指标使用页面显示单位；持续时间以秒填写</p></div></div>
        <div class="notification-add"><label for="alert-metric" class="sr-only">新增提醒项目</label><select id="alert-metric" v-model="choice"><option value="__offline">停止收到电脑上报</option><option v-for="metric in choices" :key="metricId(metric)" :value="metricId(metric)">{{ props.label(metric) }} · {{ metric.unit }}</option></select><button type="button" class="text-button" :disabled="settings.rules.length >= 20" @click="addRule">添加规则 ＋</button></div>
        <p v-if="!settings.rules.length" class="empty-state">尚无规则。可添加 CPU、GPU、内存、空闲空间或停止上报提醒。</p>
        <fieldset v-for="(rule, index) in settings.rules" :key="rule.id" class="notification-rule">
          <legend>{{ rule.kind === 'offline' ? '停止上报' : `${rule.name} · ${rule.unit}` }}</legend>
          <div class="notification-actions"><label class="check-label"><input v-model="rule.enabled" type="checkbox">此规则开启</label><button type="button" class="text-button" @click="settings.rules.splice(index, 1)">删除规则</button></div>
          <div class="notification-fields"><label>提醒名称<input v-model="rule.name" maxlength="64" required></label><label>触发持续时间（秒）<input v-model.number="rule.durationSeconds" type="number" min="2" max="86400" required></label>
            <template v-if="rule.kind === 'metric'"><label>触发条件<select v-model="rule.comparison"><option value="above">达到或超过阈值 ≥</option><option value="below">达到或低于阈值 ≤</option></select></label><label>触发阈值（{{ rule.unit }}）<input v-model.number="rule.threshold" type="number" step="any" required></label><label>恢复阈值（{{ rule.unit }}）<input v-model.number="rule.recoveryThreshold" type="number" step="any" required></label></template>
            <label>恢复持续时间（秒）<input v-model.number="rule.recoverySeconds" type="number" min="2" max="86400" required></label>
          </div>
          <label class="check-label"><input v-model="rule.notifyRecovery" type="checkbox">恢复时也发送提醒</label>
          <p v-if="rule.kind === 'offline'" class="footnote">超过 30 秒无心跳或实时数据后开始观察；失联不等于关机。服务器重启重新观察，恢复须再次持续收到上报。</p>
        </fieldset>
        <div class="notification-actions"><button class="primary-button notification-save" :disabled="busy">{{ busy ? '处理中…' : '保存通知设置' }}</button><span class="footnote">保存后重新观察条件，并取消尚未发送的旧任务。</span></div>
      </section>
    </form>
    <section class="panel notification-panel"><div class="panel-heading"><div><h2>发送记录</h2><p>最近七天，最多 100 条 · 请求至少间隔 60 秒</p></div><button class="text-button" @click="refresh">刷新 ↻</button></div><p v-if="!events.length" class="empty-state">暂无通知事件</p>
      <article v-for="event in events" :key="event.id" class="notification-event"><div class="notification-actions"><strong>{{ event.title }}</strong><span class="status-badge" :class="{ fresh: event.status === 'accepted' }">{{ statusText[event.status] ?? event.status }}</span><time>{{ date(event.createdAt) }}</time></div><p class="footnote">{{ event.detail ?? (event.status === 'queued' ? '等待额度与发送间隔检查。' : '') }}</p><details><summary>查看提醒内容</summary><p class="notification-description">{{ event.description }}</p></details></article>
    </section>
  </template>
</template>
