<script setup lang="ts">
import { useEnrollment } from './composables/useEnrollment'
import { capacity } from '../../utils/format'

const { overview, download, code, loading, busy, error, message, refresh, generate, revoke } = useEnrollment()
const date = (value: string | null | undefined) => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
const stateText: Record<string, string> = { code: '等待兑换', claimed: '等待安装', waiting: '等待真实采样', ready: '数据已验证，等待激活', active: '已接入', expired: '授权已过期' }
const dataText: Record<string, string> = { fresh: '数据最新', delayed: '更新延迟', stale: '数据过期', waiting: '等待采样', clockError: '时钟异常' }
</script>

<template>
  <p v-if="loading" class="panel empty-state" role="status">正在读取电脑接入信息…</p>
  <p v-if="error" class="error-banner" role="alert">{{ error }}</p>
  <p v-if="message" class="state-banner" role="status">{{ message }}</p>
  <template v-if="overview">
    <section class="panel notification-panel"><div class="panel-heading"><div><h2>1. 获取安装程序</h2><p>在需要监控的 Windows 11 x64 电脑上运行</p></div></div>
      <p class="footnote">向导会完成环境检查、短码绑定、后台服务安装与首次真实上报验证。无需编辑配置或安装 .NET。</p>
      <div v-if="download?.available" class="notification-actions"><a href="/api/setup/download" class="text-button">下载 Windows 安装程序 ↘</a><span class="footnote">v{{ download.version }} · {{ capacity(download.bytes) }}</span></div>
      <p v-else class="state-banner">安装包尚未发布，请稍后刷新。</p>
      <details v-if="download?.sha256"><summary class="footnote">查看安装包校验值</summary><p class="enrollment-hash">SHA-256: {{ download.sha256 }}</p><p class="footnote">当前包未签名，请从此私人页面下载并核对来源。管理员授权仅用于安装本产品后台服务。</p></details>
    </section>
    <section class="panel notification-panel"><div class="panel-heading"><div><h2>2. 授权电脑接入</h2><p>逐台添加，已有电脑继续采集</p></div></div>
      <p class="footnote">可以同时监控多台电脑。每台电脑分别安装并使用一次绑定码，新增接入不会停止已有电脑。已安装电脑的升级保留原设备身份与历史。</p>
      <div class="notification-actions"><button class="primary-button notification-save" :disabled="busy" @click="generate">{{ overview.pending && overview.pending.state !== 'active' ? '重新生成并取消旧接入' : '生成十分钟绑定码' }}</button><button v-if="overview.pending && overview.pending.state !== 'active'" class="text-button" :disabled="busy" @click="revoke">撤销本次授权</button></div>
      <div v-if="code" class="enrollment-code"><span>在安装向导输入</span><strong>{{ code.code }}</strong><small>有效至 {{ date(code.expiresAt) }}。绑定码只在生成时显示，请勿转发给他人。</small></div>
      <p v-else-if="overview.pending && overview.pending.state !== 'active'" class="footnote">一次只为一台电脑生成绑定码。已有接入授权，有效至 {{ date(overview.codeExpiresAt) }}。绑定码不能回显；重新生成会撤销原候选接入。</p>
    </section>
    <section class="panel notification-panel"><div class="panel-heading"><div><h2>3. 查看接入结果</h2><p>结果依据服务端收到的真实注册、心跳与 CPU/内存数据</p></div><button class="text-button" @click="refresh">刷新 ↻</button></div>
      <div v-if="overview.pending" class="enrollment-progress"><strong>{{ overview.pending.computerName ?? '等待向导兑换绑定码' }} · {{ stateText[overview.pending.state] ?? overview.pending.state }}</strong><p>设备注册：{{ overview.pending.registered ? '已确认' : '等待' }} · 心跳：{{ overview.pending.heartbeatReceived ? '已确认' : '等待' }} · {{ dataText[overview.pending.dataStatus] ?? overview.pending.dataStatus }}</p><p>最近采集：{{ date(overview.pending.sampledAt) }}</p><p v-if="overview.pending.detail" class="state-banner">{{ overview.pending.detail }}</p></div>
      <p v-else class="empty-state">尚无待接入电脑。先下载安装程序并生成绑定码。</p>
      <p class="footnote">安装完成不等于接入成功。请在向导完成后用手机移动流量确认电脑名称及数据更新。</p>
    </section>
  </template>
</template>
