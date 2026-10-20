<script setup lang="ts">
import { ElButton, ElCheckbox, ElCollapse, ElCollapseItem, ElInput, ElSelect, ElOption, ElSkeleton, ElSwitch, ElTag } from 'element-plus'
import { computed, ref, watch } from 'vue'
import type { HistoryMetric, Metric } from '../../types/monitor'
import type { AlertRule } from '../../types/notifications'
import { useNotifications } from './composables/useNotifications'
import { useComputerNotifications } from './composables/useComputerNotifications'
import { ruleUnit, ruleInput, ruleValue } from '../../utils/notificationUnits'
import { number } from '../../utils/format'
const props = defineProps<{ computerId: string; computerName: string; metrics: Metric[]; label: (metric: HistoryMetric) => string }>()
const { settings, events, requestsToday, budgetDate, sendKey, clearCredential, loading, ready, busy, dirty, error, save, test, refresh } = useNotifications()
const { current: computer, dirty: rulesDirty, refresh: refreshRules, save: saveRules } = useComputerNotifications(() => props.computerId)
const choice = ref('__offline')
watch(() => props.computerId, () => { choice.value = '__offline' })
const metricId = (metric: HistoryMetric) => JSON.stringify([metric.key, metric.deviceId, metric.unit])
const choices = computed(() => props.metrics.filter(metric => !metric.deviceId.startsWith('cpu/logical/')))
function addRule() {
  if (!computer.value.ready || computer.value.busy || computer.value.rules.length >= 20) return
  const metric = choices.value.find(item => metricId(item) === choice.value)
  if (!metric && choice.value !== '__offline') return
  const below = metric?.key === 'volume.free'
  const threshold = metric?.unit === '%' ? 90 : metric?.unit === 'B' ? 10 * 1024 ** 3 : metric?.unit === 'B/s' ? 10 * 1024 ** 2 : 80
  const rule: AlertRule = { id: crypto.randomUUID(), name: metric ? props.label(metric).slice(0, 64) : '停止上报', enabled: true,
    kind: metric ? 'metric' : 'offline', key: metric?.key ?? '', metricDeviceId: metric?.deviceId ?? '', unit: metric?.unit ?? '',
    comparison: below ? 'below' : 'above', threshold, durationSeconds: metric ? 60 : 300,
    recoveryThreshold: threshold * (below ? 1.1 : .9), recoverySeconds: 30, notifyRecovery: true }
  computer.value.rules.push(rule)
}
function thresholdInput(rule: AlertRule, field: 'threshold' | 'recoveryThreshold', value: string) { rule[field] = ruleValue(value, rule.unit) }
const statusText: Record<string, string> = { queued: '等待发送', sending: '发送中', accepted: '服务已接受', failed: '发送失败', unknown: '结果未知', cancelled: '已取消', expired: '已过期', limited: '额度限制' }
const date = (value: string) => new Date(value).toLocaleString('zh-CN', { hour12: false })
</script>
<template>
  <section v-if="loading" class="panel" aria-label="正在读取设置"><ElSkeleton :rows="6"/></section>
  <section v-else-if="!ready" class="panel empty-state"><p>通知设置未加载</p><ElButton @click="refresh">重新读取</ElButton></section>
  <template v-else>
    <section v-if="!computerId" class="panel empty-state">接入电脑后，可为它单独配置提醒规则。</section>
    <section v-else-if="computer.loading" class="panel" aria-label="正在读取这台电脑的规则"><ElSkeleton :rows="4"/></section>
    <section v-else-if="!computer.ready" class="panel empty-state"><p>{{ computer.error || '这台电脑的规则尚未加载' }}</p><ElButton @click="refreshRules">重新读取规则</ElButton></section>
    <form v-else class="notification-form" :inert="computer.busy || undefined" @submit.prevent="saveRules">
      <section class="panel notification-panel">
        <div class="panel-heading"><div><h2>{{ computerName || '当前电脑' }} · 提醒规则 <span class="small-count">{{ computer.rules.length }} / 20</span></h2><p>仅对这台电脑生效。条件持续满足后发送一次。</p><p class="footnote">{{ computerId }}</p></div></div>
        <p v-if="!settings.enabled" class="muted">全局发送尚未开启，保存规则后还需开启通知。</p>
        <div class="notification-add"><label for="alert-metric" class="sr-only">新增提醒项目</label><ElSelect filterable id="alert-metric" v-model="choice"><ElOption value="__offline" label="停止上报"/><ElOption v-for="metric in choices" :key="metricId(metric)" :value="metricId(metric)" :label="props.label(metric) + ' · ' + ruleUnit(metric.unit)"/></ElSelect><ElButton :disabled="computer.rules.length >= 20" @click="addRule">添加规则</ElButton></div>
        <p v-if="!computer.rules.length" class="empty-state">这台电脑还没有提醒规则。</p>
        <fieldset v-for="(rule, index) in computer.rules" :key="rule.id" class="notification-rule">
          <legend>{{ index + 1 }}. {{ rule.kind === 'offline' ? '停止上报' : rule.name }}</legend>
          <div class="rule-heading"><ElSwitch v-model="rule.enabled" active-text="启用" aria-label="启用规则"/><ElButton type="danger" link @click="computer.rules.splice(index, 1)">删除</ElButton></div>
          <p class="rule-summary">{{ rule.kind === 'offline' ? '持续失联 ' + rule.durationSeconds + ' 秒后提醒' : (rule.comparison === 'above' ? '不低于 ' : '不高于 ') + number(ruleInput(rule.threshold, rule.unit)) + ' ' + ruleUnit(rule.unit) + '，持续 ' + rule.durationSeconds + ' 秒后提醒' }}</p>
          <div class="notification-fields"><label>名称<ElInput v-model="rule.name" maxlength="64" required/></label><label>触发持续时间（秒）<ElInput v-model.number="rule.durationSeconds" type="number" min="2" max="86400" required/></label>
            <template v-if="rule.kind === 'metric'"><label>触发条件<ElSelect v-model="rule.comparison"><ElOption value="above" label="大于或等于 ≥"/><ElOption value="below" label="小于或等于 ≤"/></ElSelect></label><label>触发阈值（{{ ruleUnit(rule.unit) }}）<ElInput :model-value="ruleInput(rule.threshold, rule.unit)" type="number" step="any" required @update:model-value="thresholdInput(rule, 'threshold', $event)"/></label><label>恢复阈值（{{ ruleUnit(rule.unit) }}）<ElInput :model-value="ruleInput(rule.recoveryThreshold, rule.unit)" type="number" step="any" required @update:model-value="thresholdInput(rule, 'recoveryThreshold', $event)"/></label></template>
            <label>恢复持续时间（秒）<ElInput v-model.number="rule.recoverySeconds" type="number" min="2" max="86400" required/></label>
          </div>
          <ElCheckbox v-model="rule.notifyRecovery">恢复时提醒</ElCheckbox>
          <ElCollapse class="help-details" v-if="rule.kind === 'offline'"><ElCollapseItem title="失联如何判断" name="details"><p>心跳或数据中断超过 30 秒后开始观察，失联可能由网络或服务中断引起。服务器重启后重新计时；恢复需持续收到上报。</p></ElCollapseItem></ElCollapse>
        </fieldset>
        <ElCollapse class="help-details"><ElCollapseItem title="规则如何生效" name="details"><p>保存后只重新观察这台电脑的条件，取消它尚未发送的旧任务。其他电脑不受影响。缺失或过期的数据不计作恢复。</p></ElCollapseItem></ElCollapse>
        <div class="notification-actions"><ElButton type="primary" :disabled="computer.busy || !rulesDirty" native-type="submit">{{ computer.busy ? '保存中…' : '保存这台电脑的规则' }}</ElButton><span :class="computer.error || computer.saveError ? 'danger' : 'muted'">{{ computer.saveError || computer.error || (rulesDirty ? '有未保存的规则 · 切换电脑后保留草稿' : '规则已同步') }}</span></div>
      </section>
    </form>
    <section v-if="computerId && computer.ready" class="panel notification-panel"><div class="panel-heading"><div><h2>{{ computerName || '当前电脑' }} · 发送记录</h2><p>仅显示这台电脑 · 近 7 天 · 请求间隔至少 60 秒</p></div><ElButton type="primary" link @click="refreshRules">刷新</ElButton></div><p v-if="!computer.events.length" class="empty-state">这台电脑暂无发送记录</p>
      <article v-for="event in computer.events" :key="event.id" class="notification-event"><div class="notification-actions"><strong>{{ event.title }}</strong><ElTag disable-transitions :type="event.status === 'accepted' ? 'success' : 'info'" effect="plain">{{ statusText[event.status] ?? event.status }}</ElTag><time>{{ date(event.createdAt) }}</time></div><p v-if="event.detail" class="footnote">{{ event.detail }}</p><ElCollapse class="help-details"><ElCollapseItem title="提醒内容" name="details"><p class="notification-description">{{ event.description }}</p></ElCollapseItem></ElCollapse></article>
    </section>
    <form class="notification-form" :inert="busy || undefined" @submit.prevent="save">
      <div class="save-bar"><span :class="dirty ? 'draft-state' : error ? 'danger' : 'muted'">{{ dirty ? '全局发送设置有未保存的修改' : error ? '发送设置更新失败' : '全局发送设置已同步' }}</span><ElButton type="primary" :disabled="busy || !dirty" native-type="submit">{{ busy ? '保存中…' : '保存发送设置' }}</ElButton></div>
      <section class="panel notification-panel">
        <div class="panel-heading"><div><h2>全局发送渠道</h2><p>所有电脑共用 SendKey、发送开关和每日额度，提醒规则分别配置。</p></div><ElSwitch v-model="settings.enabled" active-text="开启通知" aria-label="开启通知"/></div>
        <div class="notification-fields"><label>SendKey<ElInput v-model="sendKey" type="password" autocomplete="new-password" maxlength="203" placeholder="SCT 开头的密钥" :disabled="clearCredential"/><small>{{ settings.hasCredential ? '已配置，留空保留。' : '尚未配置。' }} <a href="https://sct.ftqq.com/" target="_blank" rel="noopener noreferrer">获取密钥</a></small></label><label>每日请求上限<ElInput v-model.number="settings.dailyLimit" type="number" min="1" max="1000" required/><small>{{ budgetDate }} 已用 {{ requestsToday }} 次（北京时间）</small></label></div>
        <div class="notification-actions"><ElCheckbox v-model="clearCredential" @change="sendKey = ''">清除已存密钥</ElCheckbox><ElButton :disabled="busy || !settings.hasCredential || !!sendKey || clearCredential" @click="test">发送测试通知</ElButton></div>
        <p v-if="!settings.hasCredential || sendKey || clearCredential" class="muted">先保存密钥，再发送测试通知。</p>
        <ElCollapse class="help-details"><ElCollapseItem title="发送与密钥说明" name="details"><p>密钥加密保存，不会回显。通知包含电脑名称、条件、时间和面板入口。测试与失败请求也计入每日上限，所有电脑共用额度。</p><p>测试使用已保存的密钥，关闭通知时仍可测试。发送服务接受后，请在微信确认收件。</p></ElCollapseItem></ElCollapse>
      </section>
    </form>
    <section v-if="events.length" class="panel notification-panel"><div class="panel-heading"><div><h2>渠道测试与旧版记录</h2><p>全局测试，以及迁移前无法确认电脑归属的记录。</p></div></div><article v-for="event in events" :key="event.id" class="notification-event"><div class="notification-actions"><strong>{{ event.title }}</strong><ElTag disable-transitions effect="plain">{{ statusText[event.status] ?? event.status }}</ElTag><time>{{ date(event.createdAt) }}</time></div><p v-if="event.detail" class="footnote">{{ event.detail }}</p></article></section>
  </template>
</template>
