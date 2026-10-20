<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useMonitor } from './composables/useMonitor'
import { useToast } from './composables/useToast'
import { ElAlert, ElButton, ElCollapse, ElCollapseItem, ElConfigProvider, ElInput, ElSkeleton, ElTag } from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { capacity, number, rate, time, uptime, statusText } from './utils/format'
import { locationHash, readLocation, replaceQuery } from './utils/location'
import { temperatureLabel } from './utils/temperatures'
import MetricCard from './views/monitor/components/MetricCard.vue'
import ProcessTable from './views/monitor/components/ProcessTable.vue'
import TrendChart from './components/base/TrendChart.vue'
import ToastHost from './components/base/ToastHost.vue'
import HistoryView from './views/monitor/HistoryView.vue'
import NotificationsView from './views/monitor/NotificationsView.vue'
import EnrollmentView from './views/monitor/EnrollmentView.vue'
import HardwareView from './views/monitor/HardwareView.vue'
import ComputerSelector from './views/monitor/components/ComputerSelector.vue'
import TemperaturePanel from './views/monitor/components/TemperaturePanel.vue'
import ComputersView from './views/monitor/ComputersView.vue'
import type { Metric, HistoryMetric } from './types/monitor'

const initial = readLocation(window.location.hash)
const { computers, selectedComputerId, selectComputer, authenticated, setupRequired, initializing, computerLoading, busy, error, connectionError, device, latest, metrics, history, activeMetric, dataStatus, connectionStatus, cpu, gpu, memory, disk, signIn, signOut, find } = useMonitor(initial.query.get('computer') ?? '')
const toast = useToast()
const password = ref(''), view = ref(initial.page), pageRoot = ref<HTMLElement | null>(null)
const overviewTab = ref('CPU'), metricQuery = ref(''), detailChart = ref<HTMLElement | null>(null)
const nav = [{ key: 'computers', title: '电脑总览', short: '电脑' }, { key: 'overview', title: '运行总览', short: '总览' }, { key: 'detail', title: '指标详情', short: '指标' }, { key: 'processes', title: '进程列表', short: '进程' }, { key: 'history', title: '历史记录', short: '历史' }, { key: 'hardware', title: '硬件信息', short: '硬件' }, { key: 'notifications', title: '通知设置', short: '通知' }, { key: 'enrollment', title: '电脑接入', short: '接入' }] as const
const title = computed(() => nav.find(item => item.key === view.value)!.title)
const devicePage = computed(() => !['computers', 'history', 'notifications', 'enrollment'].includes(view.value))
const scopedPage = computed(() => !['computers', 'history', 'enrollment'].includes(view.value))
const statusLabel = computed(() => ({ fresh: '数据最新', delayed: '更新延迟', stale: '数据过期', waiting: '等待采样', clockError: '时钟异常' })[dataStatus.value] ?? '未知状态')
const selected = computed(() => activeMetric.value ? metrics.value.find(metric => metric.key === activeMetric.value!.key && metric.deviceId === activeMetric.value!.deviceId) ?? activeMetric.value : cpu.value)
// 总览仅使用四个百分比指标，详情选择不能改变总览的单位与坐标。
const overviewItems = computed(() => [{ label: 'CPU', metric: cpu.value, color: '#e8c58c' }, { label: 'GPU', metric: gpu.value, color: '#aeb7f1' }, { label: '内存', metric: memory.value, color: '#9adbc9' }, { label: '磁盘', metric: disk.value, color: '#9fbacf' }])
const overviewSelected = computed(() => overviewItems.value.find(item => item.label === overviewTab.value) ?? overviewItems.value[0]!)
const chartPoints = computed(() => points(selected.value))
const groupId = (metric: Metric) => metric.deviceId.startsWith('cpu/logical/') ? 'cpu/logical' : metric.deviceId
const filteredMetrics = computed(() => metrics.value.filter(metric => (metricName(metric) + ' ' + metric.name + ' ' + groupName(groupId(metric)) + (metric.unit === '°C' ? ' 温度 temperature °c' : '')).toLowerCase().includes(metricQuery.value.trim().toLowerCase())))
const detailGroups = computed(() => [...new Set(filteredMetrics.value.map(groupId))])
function syncLocation() {
  const location = readLocation(window.location.hash)
  view.value = location.page
  const computer = location.query.get('computer')
  if (computer) selectComputer(computer)
}
function skipToContent() { document.getElementById('main-content')?.focus(); window.scrollTo({ top: 0, behavior: 'instant' }) }
onMounted(() => { window.addEventListener('hashchange', syncLocation); window.addEventListener('popstate', syncLocation) })
onUnmounted(() => { window.removeEventListener('hashchange', syncLocation); window.removeEventListener('popstate', syncLocation) })
watch(view, async page => {
  const location = readLocation(window.location.hash)
  if (location.page !== page) window.history.pushState(null, '', locationHash(page, location.query))
  await nextTick()
  window.scrollTo({ top: 0, behavior: 'instant' })
  pageRoot.value?.querySelector<HTMLElement>('h1')?.focus({ preventScroll: true })
})
watch(selectedComputerId, id => { if (id) replaceQuery({ computer: id }); overviewTab.value = 'CPU' })
watch(authenticated, () => toast.clear())
function points(metric: Metric | null) { return metric ? history.value[metric.deviceId + ':' + metric.key] ?? [] : [] }
function choose(metric: Metric | null) { if (!metric) return; activeMetric.value = metric; view.value = 'detail'; void locateChart() }
function showTemperatures() { metricQuery.value = '温度'; view.value = 'detail' }
function openComputer(id: string, page: 'overview' | 'notifications') { selectComputer(id); view.value = page }
async function locateChart() {
  await nextTick()
  detailChart.value?.scrollIntoView({ block: 'start', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' })
  detailChart.value?.focus({ preventScroll: true })
}
function metricValue(metric: Metric | null) { if (!metric || metric.value === null) return '—'; return metric.unit === 'B' ? capacity(metric.value) : metric.unit === 'B/s' ? rate(metric.value) : number(metric.value) + ' ' + metric.unit }
function networkValue(id: string, key: string) { return rate(metrics.value.find(metric => metric.deviceId === id && metric.key === key)?.value) }
function groupName(group: string, useCurrentInventory = true) {
  if (group === 'cpu/logical') return 'CPU 逻辑处理器'
  if (group === 'cpu/total') return 'CPU'
  if (group === 'memory') return '内存'
  if (group.startsWith('sensor/Cpu')) return 'CPU 传感器'
  if (group.startsWith('sensor/Gpu') || group === 'gpu') return 'GPU'
  const network = useCurrentInventory ? device.value?.inventory.networks.find(item => item.id === group) : null
  if (network) return '网络 · ' + network.name
  const physical = useCurrentInventory ? device.value?.inventory.physicalDisks.find(item => item.id === group) : null
  if (physical) return '磁盘 ' + physical.index + ' · ' + physical.name
  if (group.startsWith('sensor/Storage') || group === 'disk') return '磁盘'
  if (group.startsWith('disk/')) return '磁盘 ' + group.split('/').at(-1)
  if (group.startsWith('network/')) return '网络 · ' + group
  return '分区 ' + group
}
function metricName(metric: Metric) { return metric.unit === '°C' ? temperatureLabel(metric) : metric.deviceId.startsWith('cpu/logical/') ? '逻辑处理器 ' + metric.deviceId.split('/').at(-1) : metric.name }
// 历史页可独立选择电脑，不能借用当前电脑的磁盘型号或网卡名称。
function historyMetricLabel(metric: HistoryMetric) { return groupName(metric.deviceId.startsWith('cpu/logical/') ? 'cpu/logical' : metric.deviceId, false) + ' · ' + (metric.deviceId.startsWith('cpu/logical/') ? '逻辑处理器 ' + metric.deviceId.split('/').at(-1) : metric.name) }
async function submit() { await signIn(password.value); if (authenticated.value) password.value = ''; else if (error.value) toast.error(error.value) }
async function logout() { await signOut(); if (authenticated.value && error.value) toast.error(error.value) }
</script>

<template>
  <ElConfigProvider :locale="zhCn" size="large">
  <main v-if="initializing" class="initial-screen" aria-busy="true" aria-label="正在载入监控面板">
    <div class="initial-panel"><div class="brand"><span class="brand-mark">H</span><span>Howie Monitor</span></div><ElSkeleton :rows="3"/><p role="status">正在载入监控面板…</p></div>
  </main>
  <main v-else-if="!authenticated" ref="pageRoot" class="login-screen">
    <div class="login-panel" data-enter>
      <div class="brand"><span class="brand-mark">H</span><span>Howie<span class="brand-light"> Monitor</span></span></div>
      <h1>{{ setupRequired ? '设置访问密码' : '登录监控面板' }}</h1>
      <p class="login-description">{{ setupRequired ? '首次使用，请设置至少 12 位密码。' : '查看电脑状态、进程与历史记录。' }}</p>
      <form @submit.prevent="submit">
        <label for="password">访问密码</label>
        <ElInput id="password" v-model="password" type="password" show-password :autocomplete="setupRequired ? 'new-password' : 'current-password'" :minlength="setupRequired ? 12 : 1" maxlength="128" required autofocus/>
        <p v-if="connectionError" class="error-message" role="alert">{{ connectionError }}</p>
        <ElButton type="primary" native-type="submit" :loading="busy">{{ busy ? '登录中…' : setupRequired ? '设置并登录' : '登录' }}</ElButton>
      </form>
    </div>
  </main>
  <div v-else class="app-shell">
    <a class="skip-link" href="#main-content" @click.prevent="skipToContent">跳到内容</a>
    <aside class="sidebar">
      <div class="brand"><span class="brand-mark">H</span><span>Howie<span class="brand-light"> Monitor</span></span></div>
      <button class="mobile-logout" :disabled="busy" @click="logout">退出</button>
      <nav aria-label="主导航"><button v-for="(item, index) in nav" :key="item.key" :class="{ active: view === item.key }" :aria-current="view === item.key ? 'page' : undefined" @click="view = item.key"><span class="nav-index" aria-hidden="true">{{ String(index + 1).padStart(2, '0') }}</span><span class="nav-full">{{ item.title }}</span><span class="nav-short">{{ item.short }}</span></button></nav>
      <div class="sidebar-bottom"><div class="device-mini"><span class="connection-dot" :class="{ online: connectionStatus === 'online' }"></span><div><strong>{{ device?.name ?? '尚无电脑' }}</strong><small>{{ connectionStatus === 'online' ? '采集端在线' : '采集端离线' }}</small></div></div><button class="logout-button" :disabled="busy" @click="logout">退出登录</button><span class="version">Howie Monitor · v0.1 · <a href="/third-party-notices.txt" target="_blank" rel="noopener">开源许可</a></span></div>
    </aside>
    <main id="main-content" class="workspace" tabindex="-1">
      <header class="topbar" :class="{ global: !devicePage }"><span>设备监控 <span class="separator">/</span> {{ title }}</span><div v-if="devicePage" class="topbar-right"><ElTag disable-transitions :type="dataStatus === 'fresh' ? 'success' : 'warning'" effect="plain" round>{{ statusLabel }}</ElTag><time>{{ time(latest?.sample?.sampledAt) }}</time></div><span v-else class="muted">{{ view === 'history' ? '历史查询' : view === 'notifications' ? device?.name ?? '选择电脑' : '所有电脑' }}</span></header>
      <div ref="pageRoot" class="page-content">
        <div class="page-heading" data-enter><div><h1 tabindex="-1">{{ title }}</h1><p class="page-description">{{ view === 'computers' ? '每台电脑独立展示，选择电脑查看详情。' : view === 'notifications' ? '提醒规则按电脑单独配置。' : devicePage ? (device?.name ?? '选择电脑') + ' · ' + (device?.inventory.operatingSystem ?? '') : view === 'history' ? '按电脑、指标和时间查询。' : '在 Windows 电脑上安装采集端。' }}</p></div><div v-if="devicePage && latest?.sample" class="uptime-block"><span>连续运行</span><strong>{{ uptime(latest.sample.uptimeSeconds) }}</strong></div></div>
        <ComputerSelector v-if="scopedPage" :computers="computers" :selected-id="selectedComputerId" @select="selectComputer" @enroll="view = 'enrollment'"/>
        <ElAlert v-if="connectionError" :title="connectionError" type="error" show-icon :closable="false"/>
        <div v-if="devicePage && dataStatus !== 'fresh' && latest?.sample" class="state-banner" role="status">{{ statusLabel }} · 以下为 {{ time(latest.sample.sampledAt) }} 的采样。<ElButton type="primary" link @click="view = 'enrollment'">检查电脑接入</ElButton></div>
        <ElCollapse class="help-details" v-if="devicePage && latest?.agent?.droppedSamples"><ElCollapseItem title="历史存在空缺" name="details"><p>采集端缓存已淘汰 {{ latest.agent.droppedSamples }} 个样本。</p></ElCollapseItem></ElCollapse>

        <ComputersView v-if="view === 'computers'" :computers="computers" @open="openComputer" @enroll="view = 'enrollment'"/>
        <section v-else-if="devicePage && computerLoading" class="panel computer-loading" aria-busy="true" aria-label="正在读取电脑信息"><ElSkeleton :rows="6"/></section>
        <section v-else-if="devicePage && !latest?.sample && view !== 'hardware'" class="panel welcome-state" data-enter><span class="empty-mark" aria-hidden="true">H</span><h2>{{ computers.length ? '等待这台电脑上报' : '添加你的第一台电脑' }}</h2><p>{{ computers.length ? '请确认采集端服务正在运行，收到数据后会自动更新。' : '安装 Windows 采集端后，即可查看运行状态。' }}</p><ElButton type="primary" @click="view = 'enrollment'">{{ computers.length ? '查看接入步骤' : '添加电脑' }}</ElButton><ElButton type="primary" link v-if="computers.length" @click="view = 'history'">查看已有历史</ElButton></section>
        <template v-else-if="view === 'overview'">
          <div class="metric-grid" data-enter><MetricCard title="CPU" code="01" subtitle="整机占用" :metric="cpu" :points="points(cpu)" color="#e8c58c" @select="choose(cpu)"/><MetricCard title="GPU" code="02" :subtitle="gpu?.name === 'D3D 3D' ? '3D 引擎占用' : '核心占用'" :metric="gpu" :points="points(gpu)" color="#aeb7f1" @select="choose(gpu)"/><MetricCard title="内存" code="03" :subtitle="capacity(find('memory.used')?.value) + ' / ' + capacity(find('memory.total')?.value)" :metric="memory" :points="points(memory)" color="#9adbc9" @select="choose(memory)"/><MetricCard title="磁盘" code="04" subtitle="首个物理盘活动时间" :metric="disk" :points="points(disk)" color="#9fbacf" @select="choose(disk)"/></div>
          <TemperaturePanel :metrics="metrics" :data-status="dataStatus" @select="choose" @details="showTemperatures"/>
          <section class="panel main-chart" data-enter><div class="panel-heading"><div><h2>性能变化</h2><p>最近 10 分钟 · 每 2 秒采样</p></div><div class="chart-tabs" aria-label="总览图表指标"><button v-for="item in overviewItems" :key="item.label" :class="{ active: overviewTab === item.label }" :aria-pressed="overviewTab === item.label" :disabled="!item.metric" @click="overviewTab = item.label">{{ item.label }}</button></div></div><TrendChart :key="overviewTab" :points="points(overviewSelected.metric)" :color="overviewSelected.color"/></section>
          <div class="lower-grid" data-enter><section class="panel"><div class="panel-heading"><div><h2>主要占用进程</h2><p>{{ latest?.sample?.processes.length ?? 0 }} 个进程</p></div><ElButton type="primary" link @click="view = 'processes'">查看全部</ElButton></div><ProcessTable :processes="latest?.sample?.processes ?? []" compact/></section><section class="panel network-panel"><div class="panel-heading"><h2>网络活动</h2><span class="muted">{{ dataStatus === 'fresh' ? '当前采样' : '最后采样' }}</span></div><div v-for="network in device?.inventory.networks ?? []" :key="network.id" class="network-row"><span>{{ network.name }}</span><div><span>↓ {{ networkValue(network.id, 'network.download') }}</span><span>↑ {{ networkValue(network.id, 'network.upload') }}</span></div></div><p v-if="!device?.inventory.networks.length" class="empty-state">尚无网卡信息</p></section></div>
        </template>
        <template v-else-if="view === 'detail'">
          <section ref="detailChart" class="panel detail-chart" tabindex="-1" data-enter><div class="panel-heading"><div><h2>{{ selected ? metricName(selected) : '选择指标' }}</h2><p>{{ selected ? groupName(groupId(selected)) : '' }} · 最近 10 分钟</p></div><strong class="detail-value">{{ metricValue(selected) }}</strong></div><TrendChart :key="selected?.deviceId + ':' + selected?.key" :points="chartPoints" :percent="selected?.unit === '%'" :unit="selected?.unit ?? ''"/></section>
          <div class="detail-tools" data-enter><label for="metric-search" class="sr-only">搜索指标</label><ElInput id="metric-search" v-model="metricQuery" type="search" placeholder="搜索指标或硬件"/><span class="muted">{{ filteredMetrics.length }} 个指标</span></div>
          <details v-for="group in detailGroups" :key="group" class="panel metric-detail-group" open data-enter><summary>{{ groupName(group) }}<span>{{ filteredMetrics.filter(item => groupId(item) === group).length }} 项</span></summary><div class="metric-rows"><article v-for="metric in filteredMetrics.filter(item => groupId(item) === group)" :key="metric.deviceId + ':' + metric.key" class="metric-row" :class="{ selected: selected?.key === metric.key && selected?.deviceId === metric.deviceId }"><button class="metric-row-choice" :aria-pressed="selected?.key === metric.key && selected?.deviceId === metric.deviceId" @click="choose(metric)"><span>{{ metricName(metric) }}<small>{{ statusText[metric.status] }}</small></span><strong>{{ metricValue(metric) }}</strong></button><details class="metric-meta"><summary>来源{{ metric.reason ? '与原因' : '' }}</summary><p v-if="metric.reason">{{ metric.reason }}</p><p>{{ metric.source }}</p></details></article></div></details>
          <p v-if="!detailGroups.length" class="empty-state">没有匹配的指标</p>
        </template>
        <section v-else-if="view === 'processes'" class="panel full-process" data-enter><div class="panel-heading"><h2>全部进程</h2><span class="muted">只读查看</span></div><ProcessTable :processes="latest?.sample?.processes ?? []"/></section>
        <HistoryView v-else-if="view === 'history'" :device-id="selectedComputerId || undefined" :label="historyMetricLabel"/>
        <HardwareView v-else-if="view === 'hardware'" :device="device" :metrics="metrics" :data-status="dataStatus" @select="choose" @temperatures="showTemperatures" @enroll="view = 'enrollment'"/>
        <div v-show="view === 'notifications' || view === 'enrollment'" data-enter>
          <KeepAlive><NotificationsView v-if="view === 'notifications'" :computer-id="selectedComputerId" :computer-name="device?.name ?? computers.find(computer => computer.deviceId === selectedComputerId)?.name ?? ''" :metrics="metrics" :label="historyMetricLabel"/><EnrollmentView v-else-if="view === 'enrollment'"/></KeepAlive>
        </div>
        <footer v-if="devicePage && latest?.sample" class="page-footer"><span>采样 {{ time(latest.sample.sampledAt) }}</span><span>采集耗时 {{ number(latest.sample.collectionMilliseconds) }} ms · 心跳 {{ time(latest.lastHeartbeat) }}</span></footer>
      </div>
    </main>
  </div>
  <ToastHost/>
  </ElConfigProvider>
</template>
