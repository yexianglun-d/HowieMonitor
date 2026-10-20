<script setup lang="ts">
import { ElButton, ElCard, ElCollapse, ElCollapseItem, ElDescriptions, ElDescriptionsItem, ElEmpty, ElTag } from 'element-plus'
import { capacity } from '../../utils/format'
import type { Device, Metric } from '../../types/monitor'
defineProps<{ device: Device | null; metrics: Metric[] }>()
defineEmits<{ enroll: [] }>()
</script>
<template>
  <div v-if="device" class="hardware-grid">
    <ElCard class="hardware-panel" shadow="never">
      <template #header><h2>处理器与内存</h2></template>
      <ElDescriptions :column="1" border>
        <ElDescriptionsItem v-for="processor in device.inventory.cpus" :key="processor.id" label="CPU"><strong>{{ processor.name }}</strong><p class="hardware-meta">{{ processor.physicalCores }} 核 · {{ processor.logicalProcessors }} 线程</p></ElDescriptionsItem>
        <ElDescriptionsItem label="内存">{{ capacity(metrics.find(item => item.key === 'memory.total')?.value) }}</ElDescriptionsItem>
      </ElDescriptions>
    </ElCard>
    <ElCard class="hardware-panel" shadow="never">
      <template #header><h2>采集端</h2></template>
      <ElDescriptions :column="1" border>
        <ElDescriptionsItem label="版本">{{ device.agentVersion }}</ElDescriptionsItem>
        <ElDescriptionsItem label="权限"><ElTag disable-transitions type="info" effect="plain">{{ device.sensors.isAdministrator ? '管理员' : '普通权限' }}</ElTag></ElDescriptionsItem>
        <ElDescriptionsItem label="传感器"><ElTag disable-transitions :type="device.sensors.pawnIoDeviceAccessible ? 'success' : 'warning'" effect="plain">{{ device.sensors.pawnIoDeviceAccessible ? '可访问' : '部分不可访问' }}</ElTag></ElDescriptionsItem>
      </ElDescriptions>
      <ElCollapse class="hardware-help"><ElCollapseItem title="传感器说明" name="sensors"><p>需要 PawnIO 的传感器在驱动不可用时显示缺失，具体原因见指标详情。</p></ElCollapseItem><ElCollapseItem title="网卡注册信息" name="networks"><p v-for="network in device.inventory.networks" :key="network.id">{{ network.name }} · 注册时{{ network.status === 'Up' ? '已连接' : '未连接' }}</p><p class="hardware-meta">以上为采集端注册时的硬件记录。</p></ElCollapseItem></ElCollapse>
    </ElCard>
    <ElCard class="hardware-panel" shadow="never">
      <template #header><h2>显示设备</h2></template>
      <ElDescriptions v-if="device.inventory.displayAdapters.length" :column="1" border><ElDescriptionsItem v-for="adapter in device.inventory.displayAdapters" :key="adapter.id" :label="adapter.isHardwareGpu ? 'GPU' : '虚拟设备'"><strong>{{ adapter.name }}</strong><p class="hardware-meta">{{ adapter.vendor }}</p></ElDescriptionsItem></ElDescriptions>
      <ElEmpty v-else description="尚无显卡信息" :image-size="60"/>
    </ElCard>
    <ElCard class="hardware-panel" shadow="never">
      <template #header><h2>存储</h2></template>
      <ElDescriptions :column="1" border>
        <ElDescriptionsItem v-for="drive in device.inventory.physicalDisks" :key="drive.id" :label="'磁盘 ' + drive.index"><strong>{{ drive.name }}</strong><p class="hardware-meta">{{ capacity(drive.sizeBytes) }}</p></ElDescriptionsItem>
        <ElDescriptionsItem v-for="volume in device.inventory.volumes" :key="volume.id" :label="volume.id"><strong>{{ volume.fileSystem }} · {{ capacity(volume.sizeBytes) }}</strong><p class="hardware-meta">可用 {{ capacity(metrics.find(metric => metric.key === 'volume.free' && metric.deviceId === volume.id)?.value ?? volume.freeBytes) }}</p><ElCollapse class="hardware-partitions"><ElCollapseItem title="分区信息" :name="volume.id"><p>{{ volume.partitionIds.join('，') }}</p></ElCollapseItem></ElCollapse></ElDescriptionsItem>
      </ElDescriptions>
    </ElCard>
  </div>
  <ElCard v-else class="welcome-state" shadow="never"><ElEmpty description="接入电脑后显示处理器、显卡和存储信息。"><ElButton type="primary" @click="$emit('enroll')">查看接入步骤</ElButton></ElEmpty></ElCard>
</template>
