<script setup lang="ts">
import { computed, ref } from 'vue'
import { useMonitor } from './composables/useMonitor'
import { capacity, number, rate, time, uptime, statusText } from './utils/format'
import MetricCard from './views/monitor/components/MetricCard.vue'
import ProcessTable from './views/monitor/components/ProcessTable.vue'
import TrendChart from './components/base/TrendChart.vue'
import HistoryView from './views/monitor/HistoryView.vue'
import NotificationsView from './views/monitor/NotificationsView.vue'
import EnrollmentView from './views/monitor/EnrollmentView.vue'
import ComputerSelector from './views/monitor/components/ComputerSelector.vue'
import type { Metric, HistoryMetric } from './types/monitor'

const { computers, selectedComputerId, selectComputer, authenticated, setupRequired, initializing, busy, error, device, latest, metrics, history, activeMetric, dataStatus, connectionStatus, cpu, gpu, memory, disk, signIn, signOut, find } = useMonitor()
const password = ref(''), view = ref<'overview' | 'detail' | 'processes' | 'hardware' | 'history' | 'notifications' | 'enrollment'>('overview')
const nav = [{ key: 'overview', title: '运行总览', icon: '◫' }, { key: 'detail', title: '指标详情', icon: '⌁' }, { key: 'processes', title: '进程列表', icon: '≡' }, { key: 'history', title: '历史记录', icon: '◷' }, { key: 'hardware', title: '硬件信息', icon: '▣' }, { key: 'notifications', title: '通知设置', icon: '♧' }, { key: 'enrollment', title: '电脑接入', icon: '＋' }] as const
const statusLabel = computed(() => ({ fresh: '数据最新', delayed: '更新延迟', stale: '数据过期', waiting: '等待采样', clockError: '时钟异常' })[dataStatus.value] ?? '未知状态')
const selected = computed(() => activeMetric.value ? metrics.value.find(metric => metric.key === activeMetric.value!.key && metric.deviceId === activeMetric.value!.deviceId) ?? activeMetric.value : cpu.value)
const chartPoints = computed(() => points(selected.value))
const networks = computed(() => device.value?.inventory.networks ?? [])
const groupId = (metric: Metric) => metric.deviceId.startsWith('cpu/logical/') ? 'cpu/logical' : metric.deviceId
const detailGroups = computed(() => [...new Set(metrics.value.map(groupId))])
const isStale = computed(() => dataStatus.value !== 'fresh')
function points(metric: Metric | null) { return metric ? history.value[`${metric.deviceId}:${metric.key}`] ?? [] : [] }
function choose(metric: Metric | null) { activeMetric.value = metric; view.value = 'detail' }
function metricValue(metric: Metric | null) { if (!metric || metric.value === null) return '—'; return metric.unit === 'B' ? capacity(metric.value) : metric.unit === 'B/s' ? rate(metric.value) : `${number(metric.value)} ${metric.unit}` }
function networkValue(id: string, key: string) { return rate(metrics.value.find(metric => metric.deviceId === id && metric.key === key)?.value) }
function groupName(group: string) {
  if (group === 'cpu/logical') return 'CPU 各逻辑处理器'
  if (group === 'cpu/total') return 'CPU 总览'
  if (group === 'memory') return '物理内存'
  if (group.startsWith('sensor/Cpu')) return 'CPU 传感器'
  if (group.startsWith('sensor/Gpu') || group === 'gpu') return 'GPU 传感器'
  const network = device.value?.inventory.networks.find(item => item.id === group)
  if (network) return `网络 · ${network.name}`
  const physical = device.value?.inventory.physicalDisks.find(item => item.id === group)
  if (physical) return `物理磁盘 ${physical.index} · ${physical.name}`
  if (group.startsWith('sensor/Storage') || group === 'disk') return '磁盘传感器'
  return `分区 ${group}`
}
function metricName(metric: Metric) { return metric.deviceId.startsWith('cpu/logical/') ? `逻辑处理器 ${metric.deviceId.split('/').at(-1)}` : metric.name }
function historyMetricLabel(metric: HistoryMetric) { return `${groupName(metric.deviceId.startsWith('cpu/logical/') ? 'cpu/logical' : metric.deviceId)} · ${metric.deviceId.startsWith('cpu/logical/') ? `逻辑处理器 ${metric.deviceId.split('/').at(-1)}` : metric.name}` }
async function submit() { await signIn(password.value); if (authenticated.value) password.value = '' }
</script>

<template>
  <main v-if="!authenticated" class="login-screen">
    <div class="login-atmosphere" aria-hidden="true"><span></span><span></span><span></span></div>
    <div class="login-panel"><div class="brand"><span class="brand-mark">H</span><span>Howie<span class="brand-light"> Monitor</span></span></div>
      <p class="eyebrow">PERSONAL SYSTEM MONITOR</p><h1>看见电脑的<br><span>每一次变化。</span></h1><p class="login-description">性能、进程与硬件信息，集中在一个私人面板。</p>
      <p v-if="initializing" class="muted" role="status">正在连接监控服务…</p>
      <form v-else @submit.prevent="submit"><h2>{{ setupRequired ? '创建访问密码' : '欢迎回来' }}</h2><p class="muted">{{ setupRequired ? '首次使用，设置至少 12 位密码保护你的监控数据。' : '输入你的密码，查看电脑当前状态。' }}</p>
        <label for="password">访问密码</label><input id="password" v-model="password" type="password" :autocomplete="setupRequired ? 'new-password' : 'current-password'" :minlength="setupRequired ? 12 : 1" maxlength="128" required autofocus>
        <p v-if="error" class="error-message" role="alert">{{ error }}</p><button class="primary-button" :disabled="busy">{{ busy ? '连接中…' : setupRequired ? '设置密码并进入' : '进入监控面板' }} <span>→</span></button>
      </form><p class="login-footer">HOWIE MONITOR / 私人设备 · 真实采样</p>
    </div>
  </main>
  <div v-else class="app-shell">
    <aside class="sidebar"><div class="brand"><span class="brand-mark">H</span><span>Howie<span class="brand-light"> Monitor</span></span></div><p class="nav-caption">工作空间</p>
      <nav aria-label="主导航"><button v-for="item in nav" :key="item.key" :class="{ active: view === item.key }" @click="view = item.key"><span class="nav-icon">{{ item.icon }}</span>{{ item.title }}<span v-if="view === item.key" class="nav-current">●</span></button></nav>
      <div class="sidebar-bottom"><div class="device-mini"><span class="device-symbol">▣</span><div><strong>{{ device?.name ?? '等待设备接入' }}</strong><small>{{ connectionStatus === 'online' ? '采集端已连接' : '采集端未连接' }}</small></div><span class="connection-dot" :class="{ online: connectionStatus === 'online' }"></span></div><button class="logout-button" :disabled="busy" @click="signOut">退出登录 <span>↗</span></button><span class="version">Howie Monitor · v0.1</span></div>
    </aside>
    <div class="workspace"><header class="topbar"><span>设备监控 <span class="breadcrumb-slash">/</span> {{ nav.find(item => item.key === view)?.title }}</span><div class="topbar-right"><span class="status-badge" :class="{ fresh: dataStatus === 'fresh' }"><i></i>{{ statusLabel }}</span><span class="topbar-time">{{ time(latest?.sample?.sampledAt) }}</span><button class="mobile-logout" :disabled="busy" @click="signOut">退出</button></div></header>
      <div class="page-content"><div class="page-heading"><div><p class="eyebrow">{{ view === 'overview' ? 'SYSTEM OVERVIEW' : view === 'detail' ? 'METRIC EXPLORER' : view === 'processes' ? 'PROCESS ACTIVITY' : view === 'history' ? 'HISTORY EXPLORER' : view === 'notifications' ? 'NOTIFICATION SETTINGS' : view === 'enrollment' ? 'COMPUTER CONNECTION' : 'DEVICE INVENTORY' }}</p><h1>{{ nav.find(item => item.key === view)?.title }}</h1><p class="page-description">{{ device?.name ?? '等待设备' }}<span class="separator">·</span>{{ device?.inventory.operatingSystem ?? '硬件信息尚未上报' }}</p></div><div class="uptime-block"><span>连续运行时间</span><strong>{{ uptime(latest?.sample?.uptimeSeconds) }}</strong></div></div>
        <ComputerSelector :computers="computers" :selected-id="selectedComputerId" @select="selectComputer"/>
        <div v-if="error" class="error-banner" role="alert">{{ error }} · 最后采集 {{ time(latest?.sample?.sampledAt) }}</div>
        <div v-if="isStale && view !== 'history' && view !== 'notifications' && view !== 'enrollment'" class="state-banner" role="status">{{ statusLabel }}。{{ dataStatus === 'waiting' ? '采集端接入后会自动显示真实数据。' : '以下为最后一次采样，采集时间见右上角。' }} 连接状态：{{ connectionStatus === 'online' ? '已连接' : '未连接' }}。</div>
        <div v-if="latest?.agent?.droppedSamples" class="state-banner">本地缓存已淘汰 {{ latest.agent.droppedSamples }} 个样本，历史可能存在空缺。</div>
        <template v-if="view === 'overview'">
          <div class="metric-grid"><MetricCard title="CPU" code="01" subtitle="全机逻辑处理器占用" :metric="cpu" :points="points(cpu)" color="#e8c58c" @select="choose(cpu)"/><MetricCard title="GPU" code="02" :subtitle="gpu?.name === 'D3D 3D' ? '3D 引擎占用' : 'GPU 核心占用'" :metric="gpu" :points="points(gpu)" color="#aeb7f1" @select="choose(gpu)"/><MetricCard title="内存" code="03" :subtitle="`${capacity(find('memory.used')?.value)} / ${capacity(find('memory.total')?.value)}`" :metric="memory" :points="points(memory)" color="#9adbc9" @select="choose(memory)"/><MetricCard title="磁盘" code="04" subtitle="首个物理盘活动时间" :metric="disk" :points="points(disk)" color="#9fbacf" @select="choose(disk)"/></div>
          <section class="panel main-chart"><div class="panel-heading"><div><h2>性能变化</h2><p>最近 10 分钟 · 采样间隔约 2 秒</p></div><div class="chart-tabs"><button v-for="item in [{ label: 'CPU', metric: cpu }, { label: 'GPU', metric: gpu }, { label: '内存', metric: memory }, { label: '磁盘', metric: disk }]" :key="item.label" :class="{ active: selected?.key === item.metric?.key }" @click="activeMetric = item.metric">{{ item.label }}</button></div></div><div class="chart-axis"><span>100 %</span><span>50 %</span><span>0 %</span></div><TrendChart :points="chartPoints"/><div class="chart-time"><span>{{ time(chartPoints[0]?.sampledAt) }}</span><span>{{ time(chartPoints[chartPoints.length - 1]?.sampledAt) }}</span></div></section>
          <div class="lower-grid"><section class="panel"><div class="panel-heading"><div><h2>主要占用进程 <span class="small-count">{{ latest?.sample?.processes.length ?? 0 }}</span></h2><p>实时工作集与 CPU 占用</p></div><button class="text-button" @click="view = 'processes'">查看全部 ↗</button></div><ProcessTable :processes="latest?.sample?.processes ?? []" compact/></section><section class="panel network-panel"><div class="panel-heading"><div><h2>网络活动</h2><p>各连接分别显示，流量不相加</p></div><span class="network-mark">↗↙</span></div><div v-for="network in networks" :key="network.id" class="network-row"><span>{{ network.name }}<small>{{ network.status === 'Up' ? '已连接' : '未连接' }}</small></span><div><span>↓ {{ networkValue(network.id, 'network.download') }}</span><span>↑ {{ networkValue(network.id, 'network.upload') }}</span></div></div><p v-if="!networks.length" class="empty-state">等待网络接口信息</p></section></div>
        </template>
        <template v-else-if="view === 'detail'"><section class="panel detail-chart"><div class="panel-heading"><div><h2>{{ selected ? metricName(selected) : '选择一个指标' }}</h2><p>{{ selected ? groupName(groupId(selected)) : '' }} · 最近 10 分钟</p></div><strong class="detail-value">{{ metricValue(selected) }}</strong></div><TrendChart :points="chartPoints" :percent="selected?.unit === '%'"/><div class="chart-time"><span>{{ time(chartPoints[0]?.sampledAt) }}</span><span>{{ time(chartPoints[chartPoints.length - 1]?.sampledAt) }}</span></div></section>
          <section v-for="group in detailGroups" :key="group" class="panel metric-detail-group"><h2>{{ groupName(group) }}</h2><div class="table-scroll"><table><thead><tr><th>指标</th><th>当前值</th><th>状态与原因</th><th>数据来源</th></tr></thead><tbody><tr v-for="metric in metrics.filter(item => groupId(item) === group)" :key="`${metric.deviceId}:${metric.key}`" :class="{ selected: selected?.key === metric.key && selected?.deviceId === metric.deviceId }"><td><button class="metric-link" @click="activeMetric = metric">{{ metricName(metric) }}</button></td><td>{{ metricValue(metric) }}</td><td>{{ statusText[metric.status] }}<small class="metric-reason">{{ metric.reason }}</small></td><td class="source-cell">{{ metric.source }}</td></tr></tbody></table></div></section>
        </template>
        <section v-else-if="view === 'processes'" class="panel full-process"><div class="panel-heading"><div><h2>全部进程</h2><p>{{ latest?.sample?.processes.length ?? 0 }} 个进程 · 只读查看</p></div></div><ProcessTable :processes="latest?.sample?.processes ?? []"/></section>
        <HistoryView v-else-if="view === 'history'" :key="selectedComputerId" :device-id="selectedComputerId || undefined" :label="historyMetricLabel"/>
        <NotificationsView v-else-if="view === 'notifications'" :device-id="device?.deviceId" :metrics="metrics" :label="historyMetricLabel"/>
        <EnrollmentView v-else-if="view === 'enrollment'"/>
        <template v-else><section class="panel hardware-panel"><h2>处理器与内存</h2><div v-for="processor in device?.inventory.cpus ?? []" :key="processor.id" class="hardware-item"><span>CPU</span><div><strong>{{ processor.name }}</strong><p>{{ processor.physicalCores }} 个物理核心 · {{ processor.logicalProcessors }} 个逻辑处理器</p></div></div><div class="hardware-item"><span>内存</span><strong>{{ capacity(find('memory.total')?.value) }}</strong></div></section><section class="panel hardware-panel"><h2>显示设备</h2><div v-for="adapter in device?.inventory.displayAdapters ?? []" :key="adapter.id" class="hardware-item"><span>{{ adapter.isHardwareGpu ? 'GPU' : '虚拟适配器' }}</span><div><strong>{{ adapter.name }}</strong><p>{{ adapter.vendor }}</p></div></div></section><section class="panel hardware-panel"><h2>物理磁盘与分区</h2><div v-for="drive in device?.inventory.physicalDisks ?? []" :key="drive.id" class="hardware-item"><span>物理盘 {{ drive.index }}</span><div><strong>{{ drive.name }}</strong><p>{{ capacity(drive.sizeBytes) }}</p></div></div><div v-for="volume in device?.inventory.volumes ?? []" :key="volume.id" class="hardware-item"><span>{{ volume.id }}</span><div><strong>{{ volume.fileSystem }} · {{ capacity(volume.sizeBytes) }}</strong><p>空闲 {{ capacity(metrics.find(metric => metric.key === 'volume.free' && metric.deviceId === volume.id)?.value ?? volume.freeBytes) }} · {{ volume.partitionIds.join('，') }}</p></div></div><p class="footnote">物理盘和分区容量分别记录，不重复相加。</p></section><section class="panel hardware-panel"><h2>采集能力</h2><div class="hardware-item"><span>运行权限</span><strong>{{ device?.sensors.isAdministrator ? '管理员' : '普通权限' }}</strong></div><div class="hardware-item"><span>PawnIO</span><strong>{{ device?.sensors.pawnIoDeviceAccessible ? '设备可访问' : '设备不可访问，相关传感器保留缺失状态' }}</strong></div></section></template>
        <footer class="page-footer"><span>真实采样 · 缺失值保留空缺</span><span>最近采集耗时 {{ number(latest?.sample?.collectionMilliseconds) }} ms <span class="separator">·</span> 连接心跳 {{ time(latest?.lastHeartbeat) }}</span></footer>
      </div>
    </div>
  </div>
</template>
