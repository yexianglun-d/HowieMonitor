<script setup lang="ts">
import { capacity } from '../../utils/format'
import type { Device, Metric } from '../../types/monitor'
defineProps<{ device: Device | null; metrics: Metric[] }>()
defineEmits<{ enroll: [] }>()
</script>
<template>
  <div v-if="device" class="hardware-grid" data-enter>
    <section class="panel hardware-panel"><h2>处理器与内存</h2><div v-for="processor in device?.inventory.cpus ?? []" :key="processor.id" class="hardware-item"><span>CPU</span><div><strong>{{ processor.name }}</strong><p>{{ processor.physicalCores }} 核 · {{ processor.logicalProcessors }} 线程</p></div></div><div class="hardware-item"><span>内存</span><strong>{{ capacity(metrics.find(item => item.key === 'memory.total')?.value) }}</strong></div></section>
    <section class="panel hardware-panel"><h2>显示设备</h2><div v-for="adapter in device?.inventory.displayAdapters ?? []" :key="adapter.id" class="hardware-item"><span>{{ adapter.isHardwareGpu ? 'GPU' : '虚拟设备' }}</span><div><strong>{{ adapter.name }}</strong><p>{{ adapter.vendor }}</p></div></div><p v-if="!device?.inventory.displayAdapters.length" class="empty-state">尚无显卡信息</p></section>
    <section class="panel hardware-panel"><h2>存储</h2><div v-for="drive in device?.inventory.physicalDisks ?? []" :key="drive.id" class="hardware-item"><span>磁盘 {{ drive.index }}</span><div><strong>{{ drive.name }}</strong><p>{{ capacity(drive.sizeBytes) }}</p></div></div><div v-for="volume in device?.inventory.volumes ?? []" :key="volume.id" class="hardware-item"><span>{{ volume.id }}</span><div><strong>{{ volume.fileSystem }} · {{ capacity(volume.sizeBytes) }}</strong><p>可用 {{ capacity(metrics.find(metric => metric.key === 'volume.free' && metric.deviceId === volume.id)?.value ?? volume.freeBytes) }}</p><details class="help-details"><summary>分区信息</summary><p>{{ volume.partitionIds.join('，') }}</p></details></div></div></section>
    <section class="panel hardware-panel"><h2>采集端</h2><div class="hardware-item"><span>版本</span><strong>{{ device?.agentVersion ?? '—' }}</strong></div><div class="hardware-item"><span>权限</span><strong>{{ device?.sensors.isAdministrator ? '管理员' : '普通权限' }}</strong></div><div class="hardware-item"><span>传感器</span><div><strong>{{ device?.sensors.pawnIoDeviceAccessible ? '可访问' : '部分传感器不可访问' }}</strong><details class="help-details"><summary>查看说明</summary><p>需要 PawnIO 的传感器在驱动不可用时显示缺失，具体原因见指标详情。</p></details></div></div><details class="help-details"><summary>网卡注册信息</summary><p v-for="network in device?.inventory.networks ?? []" :key="network.id">{{ network.name }} · 注册时{{ network.status === 'Up' ? '已连接' : '未连接' }}</p><p>以上为采集端注册时的硬件记录。</p></details></section>
  </div>
  <section v-else class="panel welcome-state" data-enter><h2>尚无硬件信息</h2><p>接入电脑后显示处理器、显卡和存储信息。</p><button class="primary-button" @click="$emit('enroll')">查看接入步骤</button></section>
</template>
