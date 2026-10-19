<script setup lang="ts">
import { computed, ref } from 'vue'
import type { HistoryMetric, Metric } from '../../types/monitor'
import type { AlertRule } from '../../types/notifications'
import { useNotifications } from './composables/useNotifications'
import { ruleUnit, ruleInput, ruleValue } from '../../utils/notificationUnits'
import { number } from '../../utils/format'
const props = defineProps<{ metrics: Metric[]; label: (metric: HistoryMetric) => string }>()
const { settings, events, requestsToday, budgetDate, sendKey, clearCredential, loading, ready, busy, dirty, error, save, test, refresh } = useNotifications()
const choice = ref('__offline')
const metricId = (metric: HistoryMetric) => JSON.stringify([metric.key, metric.deviceId, metric.unit])
const choices = computed(() => props.metrics.filter(metric => !metric.deviceId.startsWith('cpu/logical/')))
function addRule() {
  if (settings.value.rules.length >= 20) return
  const metric = choices.value.find(item => metricId(item) === choice.value)
  if (!metric && choice.value !== '__offline') return
  const below = metric?.key === 'volume.free'
  const threshold = metric?.unit === '%' ? 90 : metric?.unit === 'B' ? 10 * 1024 ** 3 : metric?.unit === 'B/s' ? 10 * 1024 ** 2 : 80
  const rule: AlertRule = { id: crypto.randomUUID(), name: metric ? props.label(metric).slice(0, 64) : '停止上报', enabled: true,
    kind: metric ? 'metric' : 'offline', key: metric?.key ?? '', metricDeviceId: metric?.deviceId ?? '', unit: metric?.unit ?? '',
    comparison: below ? 'below' : 'above', threshold, durationSeconds: metric ? 60 : 300,
    recoveryThreshold: threshold * (below ? 1.1 : .9), recoverySeconds: 30, notifyRecovery: true }
  settings.value.rules.push(rule)
}
function thresholdInput(rule: AlertRule, field: 'threshold' | 'recoveryThreshold', event: Event) { rule[field] = ruleValue((event.target as HTMLInputElement).value, rule.unit) }
const statusText: Record<string, string> = { queued: '等待发送', sending: '发送中', accepted: '服务已接受', failed: '发送失败', unknown: '结果未知', cancelled: '已取消', expired: '已过期', limited: '额度限制' }
const date = (value: string) => new Date(value).toLocaleString('zh-CN', { hour12: false })
</script>
<template>
  <p v-if="loading" class="panel empty-state" role="status">正在读取设置…</p>
  <section v-else-if="!ready" class="panel empty-state"><p>通知设置未加载</p><button class="secondary-button" @click="refresh">重新读取</button></section>
  <template v-else>
    <form class="notification-form" :inert="busy || undefined" @submit.prevent="save">
      <div class="save-bar"><span :class="dirty ? 'draft-state' : error ? 'danger' : 'muted'">{{ dirty ? '有未保存的修改 · 切页后保留草稿' : error ? '通知数据更新失败' : '设置已同步' }}</span><button class="primary-button" :disabled="busy || !dirty">{{ busy ? '保存中…' : '保存设置' }}</button></div>
      <section class="panel notification-panel">
        <div class="panel-heading"><div><h2>微信通知</h2><p>通过 Server酱 Turbo 发送</p></div><label class="check-label"><input v-model="settings.enabled" type="checkbox" role="switch">开启通知</label></div>
        <div class="notification-fields"><label>SendKey<input v-model="sendKey" type="password" autocomplete="new-password" maxlength="203" placeholder="SCT 开头的密钥" :disabled="clearCredential"><small>{{ settings.hasCredential ? '已配置，留空保留。' : '尚未配置。' }} <a href="https://sct.ftqq.com/" target="_blank" rel="noopener noreferrer">获取密钥</a></small></label><label>每日请求上限<input v-model.number="settings.dailyLimit" type="number" min="1" max="1000" required><small>{{ budgetDate }} 已用 {{ requestsToday }} 次（北京时间）</small></label></div>
        <div class="notification-actions"><label class="check-label"><input v-model="clearCredential" type="checkbox" @change="sendKey = ''">清除已存密钥</label><button type="button" class="secondary-button" :disabled="busy || !settings.hasCredential || !!sendKey || clearCredential" @click="test">发送测试通知</button></div>
        <p v-if="!settings.hasCredential || sendKey || clearCredential" class="muted">先保存密钥，再发送测试通知。</p>
        <details class="help-details"><summary>发送与密钥说明</summary><p>密钥加密保存，不会回显。通知包含电脑名称、条件、时间和面板入口。测试与失败请求也计入每日上限，所有电脑共用额度。</p><p>测试使用已保存的密钥，关闭通知时仍可测试。发送服务接受后，请在微信确认收件。</p></details>
      </section>
      <section class="panel notification-panel">
        <div class="panel-heading"><div><h2>提醒规则 <span class="small-count">{{ settings.rules.length }} / 20</span></h2><p>条件持续满足后发送一次，恢复提醒单独设置。</p></div></div>
        <div class="notification-add"><label for="alert-metric" class="sr-only">新增提醒项目</label><select id="alert-metric" v-model="choice"><option value="__offline">停止上报</option><option v-for="metric in choices" :key="metricId(metric)" :value="metricId(metric)">{{ props.label(metric) }} · {{ ruleUnit(metric.unit) }}</option></select><button type="button" class="secondary-button" :disabled="settings.rules.length >= 20" @click="addRule">添加规则</button></div>
        <p v-if="!settings.rules.length" class="empty-state">还没有规则，选择一个提醒项目开始。</p>
        <fieldset v-for="(rule, index) in settings.rules" :key="rule.id" class="notification-rule">
          <legend>{{ index + 1 }}. {{ rule.kind === 'offline' ? '停止上报' : rule.name }}</legend>
          <div class="rule-heading"><label class="check-label"><input v-model="rule.enabled" type="checkbox" role="switch">启用</label><button type="button" class="text-button danger" @click="settings.rules.splice(index, 1)">删除</button></div>
          <p class="rule-summary">{{ rule.kind === 'offline' ? '持续失联 ' + rule.durationSeconds + ' 秒后提醒' : (rule.comparison === 'above' ? '不低于 ' : '不高于 ') + number(ruleInput(rule.threshold, rule.unit)) + ' ' + ruleUnit(rule.unit) + '，持续 ' + rule.durationSeconds + ' 秒后提醒' }}</p>
          <div class="notification-fields"><label>名称<input v-model="rule.name" maxlength="64" required></label><label>触发持续时间（秒）<input v-model.number="rule.durationSeconds" type="number" min="2" max="86400" required></label>
            <template v-if="rule.kind === 'metric'"><label>触发条件<select v-model="rule.comparison"><option value="above">大于或等于 ≥</option><option value="below">小于或等于 ≤</option></select></label><label>触发阈值（{{ ruleUnit(rule.unit) }}）<input :value="ruleInput(rule.threshold, rule.unit)" type="number" step="any" required @input="thresholdInput(rule, 'threshold', $event)"></label><label>恢复阈值（{{ ruleUnit(rule.unit) }}）<input :value="ruleInput(rule.recoveryThreshold, rule.unit)" type="number" step="any" required @input="thresholdInput(rule, 'recoveryThreshold', $event)"></label></template>
            <label>恢复持续时间（秒）<input v-model.number="rule.recoverySeconds" type="number" min="2" max="86400" required></label>
          </div>
          <label class="check-label"><input v-model="rule.notifyRecovery" type="checkbox">恢复时提醒</label>
          <details v-if="rule.kind === 'offline'" class="help-details"><summary>失联如何判断</summary><p>心跳或数据中断超过 30 秒后开始观察，失联可能由网络或服务中断引起。服务器重启后重新计时；恢复需持续收到上报。</p></details>
        </fieldset>
        <details class="help-details"><summary>规则如何生效</summary><p>保存后重新观察条件，取消未发送的旧任务。缺失或过期的数据不计作恢复。硬件专属指标只在能读取该指标的电脑上生效。</p></details>
        <div v-if="settings.rules.length" class="notification-actions"><button class="primary-button" :disabled="busy || !dirty">保存设置</button></div>
      </section>
    </form>
    <section class="panel notification-panel"><div class="panel-heading"><div><h2>发送记录</h2><p>近 7 天 · 最多 100 条 · 请求间隔至少 60 秒</p></div><button class="text-button" @click="refresh">刷新</button></div><p v-if="!events.length" class="empty-state">暂无发送记录</p>
      <article v-for="event in events" :key="event.id" class="notification-event"><div class="notification-actions"><strong>{{ event.title }}</strong><span class="status-badge" :class="{ fresh: event.status === 'accepted' }">{{ statusText[event.status] ?? event.status }}</span><time>{{ date(event.createdAt) }}</time></div><p v-if="event.detail" class="footnote">{{ event.detail }}</p><details class="help-details"><summary>提醒内容</summary><p class="notification-description">{{ event.description }}</p></details></article>
    </section>
  </template>
</template>
