<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useEnrollment } from './composables/useEnrollment'
import { capacity } from '../../utils/format'
import { usePageMotion } from '../../composables/usePageMotion'
import { useToast } from '../../composables/useToast'
const { overview, download, code, loading, busy, error, refresh, generate, revoke } = useEnrollment()
const toast = useToast()
const now = ref(Date.now()), replacing = ref(false), codePanel = ref<HTMLElement | null>(null)
let clock: ReturnType<typeof setInterval> | undefined, disposed = false
onMounted(() => { clock = setInterval(() => { now.value = Date.now() }, 1000) })
onUnmounted(() => { disposed = true; clearInterval(clock) })
const remaining = computed(() => code.value ? Math.max(0, Math.ceil((Date.parse(code.value.expiresAt) - now.value) / 1000)) : 0)
const hasPending = computed(() => overview.value?.pending && !['active', 'expired'].includes(overview.value.pending.state) && (!overview.value.codeExpiresAt || Date.parse(overview.value.codeExpiresAt) > now.value))
const stateText: Record<string, string> = { code: '等待输入绑定码', claimed: '等待安装', waiting: '等待采样', ready: '等待激活', active: '已接入', expired: '授权已过期' }
const date = (value: string | null | undefined) => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
usePageMotion(codePanel, () => code.value?.code ?? '')
async function copyCode() {
  if (!code.value || !remaining.value) return
  try { await navigator.clipboard.writeText(code.value.code); if (!disposed) toast.success('绑定码已复制') }
  catch { if (!disposed) toast.error('复制失败，请长按绑定码手动复制。') }
}
async function generateCode() { replacing.value = false; await generate(); now.value = Date.now() }
</script>
<template>
  <p v-if="loading" class="panel empty-state" role="status">正在读取接入信息…</p>
  <section v-else-if="!overview && error" class="panel empty-state"><p>接入信息未加载</p><button class="secondary-button" @click="refresh">重新读取</button></section>
  <p v-if="overview && error" class="footnote danger" role="status">接入信息更新失败，正在重试。<button class="text-button" @click="refresh">立即刷新</button></p>
  <template v-if="overview">
    <section class="panel enrollment-step"><div class="step-number" aria-hidden="true">01</div><div class="step-content"><div class="panel-heading"><div><h2>下载安装程序</h2><p>Windows 11 x64 · 无需另装 .NET</p></div></div>
      <div v-if="download?.available" class="notification-actions"><a href="/api/setup/download" class="primary-button">下载安装程序</a><span class="muted">v{{ download.version }} · {{ capacity(download.bytes) }}</span></div>
      <div v-else class="info-banner"><span>服务端尚未提供安装包。</span><button class="text-button" @click="refresh">刷新检查</button></div>
      <details class="help-details"><summary>安装说明与校验</summary><p>向导会检查环境、绑定电脑、安装后台服务并验证首次上报。管理员权限用于安装服务；已安装电脑升级时保留身份与历史。</p><template v-if="download?.sha256"><p>当前包未签名，请从本页面下载并核对来源。</p><p class="enrollment-hash">SHA-256: {{ download.sha256 }}</p></template></details>
    </div></section>
    <section class="panel enrollment-step"><div class="step-number" aria-hidden="true">02</div><div class="step-content"><div class="panel-heading"><div><h2>绑定电脑</h2><p>在安装向导中输入绑定码，每台电脑使用一次。</p></div></div>
      <div v-if="code" ref="codePanel" class="enrollment-code" data-enter><span>绑定码</span><strong>{{ code.code }}</strong><div class="code-actions"><span role="timer">{{ remaining ? '剩余 ' + Math.floor(remaining / 60) + ':' + String(remaining % 60).padStart(2, '0') : '已过期，请重新生成' }}</span><button class="primary-button" :disabled="!remaining" @click="copyCode">复制绑定码</button></div><small>请勿将绑定码转发给他人。</small></div>
      <p v-else-if="hasPending" class="info-banner">已有授权，有效至 {{ date(overview.codeExpiresAt) }}。绑定码无法再次显示。</p>
      <div v-if="replacing" class="state-banner"><p>重新生成会撤销当前绑定码及尚未完成的接入。</p><div class="notification-actions"><button class="secondary-button" :disabled="busy" @click="generateCode">确认重新生成</button><button class="text-button" @click="replacing = false">保留当前授权</button></div></div>
      <div v-else class="notification-actions"><button :class="hasPending ? 'text-button' : 'primary-button'" :disabled="busy" @click="hasPending ? replacing = true : generateCode()">{{ busy ? '处理中…' : hasPending ? '重新生成绑定码' : '生成绑定码（10 分钟）' }}</button><button v-if="hasPending" class="text-button danger" :disabled="busy" @click="revoke">撤销授权</button></div>
      <details class="help-details"><summary>多台电脑如何接入</summary><p>每台电脑分别安装并绑定。添加电脑或撤销未完成的授权，不影响已有电脑采集。</p></details>
    </div></section>
    <section class="panel enrollment-step"><div class="step-number" aria-hidden="true">03</div><div class="step-content"><div class="panel-heading"><div><h2>确认接入</h2><p>收到注册、心跳和 CPU / 内存采样后确认。</p></div><button class="text-button" @click="refresh">刷新</button></div>
      <div v-if="overview.pending" class="enrollment-progress"><strong>{{ overview.pending.computerName ?? '新电脑' }}</strong><span class="status-badge" :class="{ fresh: overview.pending.state === 'active' }">{{ stateText[overview.pending.state] ?? overview.pending.state }}</span><div class="enrollment-checks"><span :class="{ done: overview.pending.registered }">注册 {{ overview.pending.registered ? '已收到' : '等待中' }}</span><span :class="{ done: overview.pending.heartbeatReceived }">心跳 {{ overview.pending.heartbeatReceived ? '已收到' : '等待中' }}</span><span :class="{ done: overview.pending.dataStatus === 'fresh' }">采样 {{ overview.pending.sampledAt ? date(overview.pending.sampledAt) : '等待中' }}</span></div><p v-if="overview.pending.detail" class="muted">{{ overview.pending.detail }}</p></div>
      <p v-else class="empty-state">生成绑定码后，可在这里查看进度。</p>
      <details class="help-details"><summary>如何确认远程访问可用</summary><p>向导完成后，用手机移动网络打开本面板，检查电脑名称及数据是否更新。</p></details>
    </div></section>
  </template>
</template>
