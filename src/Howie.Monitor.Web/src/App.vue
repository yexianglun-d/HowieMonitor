<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useMonitor } from './composables/useMonitor'
import { useToast } from './composables/useToast'
import { useScrollExperience } from './composables/useScrollExperience'
import ScrollNavigator from './components/base/ScrollNavigator.vue'
import { usePageMotion } from './composables/usePageMotion'
import { ElAlert, ElButton, ElCollapse, ElCollapseItem, ElConfigProvider, ElInput, ElSkeleton, ElTag } from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { capacity, number, rate, time, uptime, statusText } from './utils/format'
import { locationHash, readLocation, replaceQuery } from './utils/location'
import { temperatureLabel } from './utils/temperatures'
import { gpuUsageLabel } from './utils/gpus'
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
import RemoveComputerDialog from './views/monitor/components/RemoveComputerDialog.vue'
import type { Metric, HistoryMetric } from './types/monitor'

const TerminalView = defineAsyncComponent(() => import('./views/monitor/TerminalView.vue'))
const initial = readLocation(window.location.hash)
const { computers, selectedComputerId, selectComputer, authenticated, setupRequired, initializing, computerLoading, busy, error, connectionError, device, latest, metrics, history, activeMetric, dataStatus, connectionStatus, cpu, gpus, memory, disk, signIn, signOut, forgetComputer, find } = useMonitor(initial.query.get('computer') ?? '')
const toast = useToast()
const password = ref(''), view = ref(initial.page), pageRoot = ref<HTMLElement | null>(null), viewRoot = ref<HTMLElement | null>(null)
const contextToolbar = ref<HTMLElement | null>(null)
const { sections, active: activeSection, stuck: toolbarStuck, go: goSection } = useScrollExperience(pageRoot, viewRoot, contextToolbar, () => `${authenticated.value}:${view.value}:${selectedComputerId.value}`)
const removalTarget = ref<{ deviceId: string; name: string | null } | null>(null), removedComputerIds = ref<string[]>([]), historyRevision = ref(0)
const overviewTab = ref('CPU'), metricQuery = ref(''), detailChart = ref<HTMLElement | null>(null)
const nav = [{ key: 'computers', title: '电脑总览', short: '电脑' }, { key: 'overview', title: '运行总览', short: '总览' }, { key: 'detail', title: '指标详情', short: '指标' }, { key: 'processes', title: '进程列表', short: '进程' }, { key: 'history', title: '历史记录', short: '历史' }, { key: 'hardware', title: '硬件信息', short: '硬件' }, { key: 'notifications', title: '通知设置', short: '通知' }, { key: 'terminal', title: '远程终端', short: '终端' }, { key: 'enrollment', title: '电脑接入', short: '接入' }] as const
const title = computed(() => nav.find(item => item.key === view.value)!.title)
const devicePage = computed(() => !['computers', 'history', 'notifications', 'terminal', 'enrollment'].includes(view.value))
const scopedPage = computed(() => !['computers', 'history', 'enrollment'].includes(view.value))
const statusLabel = computed(() => ({ fresh: '数据最新', delayed: '更新延迟', stale: '数据过期', waiting: '等待采样', clockError: '时钟异常' })[dataStatus.value] ?? '未知状态')
const selected = computed(() => activeMetric.value ? metrics.value.find(metric => metric.key === activeMetric.value!.key && metric.deviceId === activeMetric.value!.deviceId) ?? activeMetric.value : cpu.value)
// 每张显卡绑定自己的原始指标，详情选择不能改变总览的单位或串用另一张显卡的曲线。
const overviewItems = computed(() => [
  { id: 'CPU', label: 'CPU', metric: cpu.value, color: '#e8c58c', subtitle: '整机占用', hardware: '', code: '01' },
  ...(gpus.value.length ? gpus.value.map((gpu, index) => ({ id: gpu.deviceId, label: gpus.value.length > 1 ? 'GPU ' + (index + 1) : 'GPU',
    metric: gpu.usage, color: '#aeb7f1', subtitle: gpuUsageLabel(gpu.usage), hardware: gpu.name, code: '02.' + (index + 1) }))
    : [{ id: 'GPU', label: 'GPU', metric: null, color: '#aeb7f1', subtitle: '尚无占用上报', hardware: '', code: '02' }]),
  { id: '内存', label: '内存', metric: memory.value, color: '#9adbc9', subtitle: capacity(find('memory.used')?.value) + ' / ' + capacity(find('memory.total')?.value), hardware: '', code: '03' },
  { id: '磁盘', label: '磁盘', metric: disk.value, color: '#9fbacf', subtitle: '首个物理盘活动时间', hardware: '', code: '04' },
])
const overviewSelected = computed(() => overviewItems.value.find(item => item.id === overviewTab.value) ?? overviewItems.value[0]!)
const chartPoints = computed(() => points(selected.value))
const groupId = (metric: Metric) => metric.deviceId.startsWith('cpu/logical/') ? 'cpu/logical' : metric.deviceId
const filteredMetrics = computed(() => metrics.value.filter(metric => (metricName(metric) + ' ' + metric.name + ' ' + groupName(groupId(metric)) + (metric.unit === '°C' ? ' 温度 temperature °c' : '')).toLowerCase().includes(metricQuery.value.trim().toLowerCase())))
const detailGroups = computed(() => [...new Set(filteredMetrics.value.map(groupId))])
usePageMotion(viewRoot, () => `${authenticated.value}:${view.value}:${selectedComputerId.value}`)
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
watch(selectedComputerId, id => { replaceQuery({ computer: id || null }); overviewTab.value = 'CPU' })
watch(authenticated, value => { toast.clear(); if (!value) { removalTarget.value = null; removedComputerIds.value = [] } })
function points(metric: Metric | null) { return metric ? history.value[metric.deviceId + ':' + metric.key] ?? [] : [] }
function choose(metric: Metric | null) { if (!metric) return; activeMetric.value = metric; view.value = 'detail'; void locateChart() }
function removedComputer(id: string) {
  removedComputerIds.value = [...removedComputerIds.value, id]; historyRevision.value++
  const location = readLocation(window.location.hash)
  if (location.query.get('historyComputer') === id) replaceQuery({ historyComputer: null, metric: null })
  forgetComputer(id)
}
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
  if (group.startsWith('sensor/Gpu') || group === 'gpu') return useCurrentInventory ? gpus.value.find(gpu => gpu.deviceId === group)?.name ?? 'GPU' : 'GPU'
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
      <div class="sidebar-bottom"><div class="device-mini"><span class="connection-dot" :class="{ online: connectionStatus === 'online' }"></span><div><strong>{{ device?.name ?? '尚无电脑' }}</strong><small>{{ connectionStatus === 'online' ? '采集端在线' : '采集端离线' }}</small></div></div><button class="logout-button" :disabled="busy" @click="logout">退出登录</button><span class="version">Howie Monitor · <a href="/third-party-notices.txt" target="_blank" rel="noopener">开源许可</a></span></div>
    </aside>
    <main id="main-content" class="workspace" tabindex="-1">
      <header class="topbar" :class="{ global: !devicePage }"><span>设备监控 <span class="separator">/</span> {{ title }}</span><div v-if="devicePage" class="topbar-right"><ElTag disable-transitions :type="dataStatus === 'fresh' ? 'success' : 'warning'" effect="plain" round>{{ statusLabel }}</ElTag><time>{{ time(latest?.sample?.sampledAt) }}</time></div><span v-else class="muted">{{ view === 'history' ? '历史查询' : view === 'notifications' || view === 'terminal' ? device?.name ?? '选择电脑' : '所有电脑' }}</span></header>
      <div ref="pageRoot" class="page-content">
        <div class="page-heading" data-enter><div class="heading-copy"><span class="page-eyebrow">HOWIE / {{ String(nav.findIndex(item => item.key === view) + 1).padStart(2, '0') }}</span><div class="heading-contour" data-parallax aria-hidden="true"><i/><i/><i/></div><h1 tabindex="-1">{{ title }}</h1><p class="page-description">{{ view === 'computers' ? '每台电脑独立展示，选择电脑查看详情。' : view === 'notifications' ? '提醒规则按电脑单独配置。' : view === 'terminal' ? '连接所选电脑的管理员 PowerShell。' : devicePage ? (device?.name ?? '选择电脑') + ' · ' + (device?.inventory.operatingSystem ?? '') : view === 'history' ? '按电脑、指标和时间查询。' : '在 Windows 电脑上安装采集端。' }}</p></div><div v-if="devicePage && latest?.sample" class="uptime-block"><span>连续运行</span><strong>{{ uptime(latest.sample.uptimeSeconds) }}</strong></div></div>
        <div ref="contextToolbar" class="context-toolbar" :class="{ 'is-stuck': toolbarStuck, 'has-sections': sections.length > 1 }" v-show="scopedPage || sections.length > 1">
          <ComputerSelector v-if="scopedPage" :computers="computers" :selected-id="selectedComputerId" @select="selectComputer" @enroll="view = 'enrollment'"/>
          <ScrollNavigator :sections="sections" :active="activeSection" @go="goSection"/>
        </div>
        <ElAlert v-if="connectionError" :title="connectionError" type="error" show-icon :closable="false"/>
        <div v-if="devicePage && dataStatus !== 'fresh' && latest?.sample" class="state-banner" role="status">{{ statusLabel }} · 以下为 {{ time(latest.sample.sampledAt) }} 的采样。<ElButton type="primary" link @click="view = 'enrollment'">检查电脑接入</ElButton></div>
        <ElCollapse class="help-details" v-if="devicePage && latest?.agent?.droppedSamples"><ElCollapseItem title="历史存在空缺" name="details"><p>采集端缓存已淘汰 {{ latest.agent.droppedSamples }} 个样本。</p></ElCollapseItem></ElCollapse>

        <div ref="viewRoot" class="view-content" :data-view="view">
        <ComputersView v-if="view === 'computers'" :computers="computers" @remove="computer => removalTarget = computer" @open="openComputer" @enroll="view = 'enrollment'"/>
        <section v-else-if="devicePage && computerLoading" class="panel computer-loading" aria-busy="true" aria-label="正在读取电脑信息"><ElSkeleton :rows="6"/></section>
        <section v-else-if="devicePage && !latest?.sample && view !== 'hardware'" class="panel welcome-state" data-enter><span class="empty-mark" aria-hidden="true">H</span><h2>{{ computers.length ? '等待这台电脑上报' : '添加你的第一台电脑' }}</h2><p>{{ computers.length ? '请确认采集端服务正在运行，收到数据后会自动更新。' : '安装 Windows 采集端后，即可查看运行状态。' }}</p><ElButton type="primary" @click="view = 'enrollment'">{{ computers.length ? '查看接入步骤' : '添加电脑' }}</ElButton><ElButton type="primary" link v-if="computers.length" @click="view = 'history'">查看已有历史</ElButton></section>
        <template v-else-if="view === 'overview'">
          <div class="metric-grid" data-scroll-section="实时指标" data-enter><MetricCard v-for="item in overviewItems" :key="item.id" :title="item.label" :code="item.code" :hardware="item.hardware" :subtitle="item.subtitle" :metric="item.metric" :points="points(item.metric)" :color="item.color" @select="choose(item.metric)"/></div>
          <TemperaturePanel :metrics="metrics" :data-status="dataStatus" @select="choose" @details="showTemperatures"/>
          <section class="panel main-chart" data-scroll-section="性能变化" data-enter><div class="panel-heading"><div><h2>性能变化</h2><p>{{ overviewSelected.hardware ? overviewSelected.hardware + ' · ' : '' }}最近 10 分钟 · 每 2 秒采样</p></div><div class="chart-tabs" aria-label="总览图表指标"><button v-for="item in overviewItems" :key="item.id" :title="item.hardware || item.label" :class="{ active: overviewSelected.id === item.id }" :aria-pressed="overviewSelected.id === item.id" :disabled="!item.metric" @click="overviewTab = item.id">{{ item.label }}</button></div></div><TrendChart :key="overviewSelected.id" :points="points(overviewSelected.metric)" :color="overviewSelected.color"/></section>
          <div class="lower-grid" data-scroll-section="进程与网络" data-enter><section class="panel"><div class="panel-heading"><div><h2>主要占用进程</h2><p>{{ latest?.sample?.processes.length ?? 0 }} 个进程</p></div><ElButton type="primary" link @click="view = 'processes'">查看全部</ElButton></div><ProcessTable :processes="latest?.sample?.processes ?? []" compact/></section><section class="panel network-panel"><div class="panel-heading"><h2>网络活动</h2><span class="muted">{{ dataStatus === 'fresh' ? '当前采样' : '最后采样' }}</span></div><div v-for="network in device?.inventory.networks ?? []" :key="network.id" class="network-row"><span>{{ network.name }}</span><div><span>↓ {{ networkValue(network.id, 'network.download') }}</span><span>↑ {{ networkValue(network.id, 'network.upload') }}</span></div></div><p v-if="!device?.inventory.networks.length" class="empty-state">尚无网卡信息</p></section></div>
        </template>
        <template v-else-if="view === 'detail'">
          <section ref="detailChart" class="panel detail-chart" data-scroll-section="指标曲线" tabindex="-1" data-enter><div class="panel-heading"><div><h2>{{ selected ? metricName(selected) : '选择指标' }}</h2><p>{{ selected ? groupName(groupId(selected)) : '' }} · 最近 10 分钟</p></div><strong class="detail-value">{{ metricValue(selected) }}</strong></div><TrendChart :key="selected?.deviceId + ':' + selected?.key" :points="chartPoints" :percent="selected?.unit === '%'" :unit="selected?.unit ?? ''"/></section>
          <div class="detail-tools" data-enter><label for="metric-search" class="sr-only">搜索指标</label><ElInput id="metric-search" v-model="metricQuery" type="search" placeholder="搜索指标或硬件"/><span class="muted">{{ filteredMetrics.length }} 个指标</span></div>
          <details v-for="group in detailGroups" :key="group" class="panel metric-detail-group" :data-scroll-section="groupName(group)" open data-enter><summary>{{ groupName(group) }}<span>{{ filteredMetrics.filter(item => groupId(item) === group).length }} 项</span></summary><div class="metric-rows"><article v-for="metric in filteredMetrics.filter(item => groupId(item) === group)" :key="metric.deviceId + ':' + metric.key" class="metric-row" :class="{ selected: selected?.key === metric.key && selected?.deviceId === metric.deviceId }"><button class="metric-row-choice" :aria-pressed="selected?.key === metric.key && selected?.deviceId === metric.deviceId" @click="choose(metric)"><span>{{ metricName(metric) }}<small>{{ statusText[metric.status] }}</small></span><strong>{{ metricValue(metric) }}</strong></button><details class="metric-meta"><summary>来源{{ metric.reason ? '与原因' : '' }}</summary><p v-if="metric.reason">{{ metric.reason }}</p><p>{{ metric.source }}</p></details></article></div></details>
          <p v-if="!detailGroups.length" class="empty-state">没有匹配的指标</p>
        </template>
        <section v-else-if="view === 'processes'" class="panel full-process" data-enter><div class="panel-heading"><h2>全部进程</h2><span class="muted">只读查看</span></div><ProcessTable :processes="latest?.sample?.processes ?? []"/></section>
        <HistoryView v-else-if="view === 'history'" :key="historyRevision" @remove="computer => removalTarget = computer" :device-id="selectedComputerId || undefined" :label="historyMetricLabel"/>
        <HardwareView v-else-if="view === 'hardware'" :device="device" :metrics="metrics" :data-status="dataStatus" @select="choose" @temperatures="showTemperatures" @enroll="view = 'enrollment'"/>
        <TerminalView v-else-if="view === 'terminal'" :key="selectedComputerId" :computer-id="selectedComputerId" :computer-name="device?.name ?? computers.find(computer => computer.deviceId === selectedComputerId)?.name ?? ''" @enroll="view = 'enrollment'"/>
        <div v-show="view === 'notifications' || view === 'enrollment'" class="settings-content" data-enter>
          <KeepAlive><NotificationsView v-if="view === 'notifications'" :removed-computer-ids="removedComputerIds" :computer-id="selectedComputerId" :computer-name="device?.name ?? computers.find(computer => computer.deviceId === selectedComputerId)?.name ?? ''" :metrics="metrics" :label="historyMetricLabel"/><EnrollmentView v-else-if="view === 'enrollment'"/></KeepAlive>
        </div>
        </div>
        <footer v-if="devicePage && latest?.sample" class="page-footer"><span>采样 {{ time(latest.sample.sampledAt) }}</span><span>采集耗时 {{ number(latest.sample.collectionMilliseconds) }} ms · 心跳 {{ time(latest.lastHeartbeat) }}</span></footer>
      </div>
    </main>
  </div>
  <RemoveComputerDialog v-if="authenticated" :target="removalTarget" @close="removalTarget = null" @removed="removedComputer"/>
  <ToastHost/>
  </ElConfigProvider>
</template>
