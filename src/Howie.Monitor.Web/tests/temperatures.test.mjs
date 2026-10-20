import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'
import { renderToString } from 'vue/server-renderer'
import { createServer } from 'vite'

const source = await readFile(new URL('../src/utils/temperatures.ts', import.meta.url), 'utf8')
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const sandbox = { exports: {} }
vm.runInNewContext(code, sandbox)
const { temperatureAvailable, temperatureGroups, temperatureLabel, temperatureReason, temperatureThreshold } = sandbox.exports
function metric(deviceId, name, value, status = 'available') {
  return { key: deviceId + '/Temperature/' + name, deviceId, name, value, status, unit: '°C',
    source: 'LibreHardwareMonitorLib 0.9.6: ' + deviceId.split('/').slice(1).join(' ') + '/Temperature', reason: null }
}
const readings = () => [
  { ...metric('sensor/Cpu/0', 'Core (Tctl/Tdie)', null, 'driverMissing'), reason: 'PawnIO unavailable, error 2' },
  metric('sensor/GpuAmd/1', 'GPU VR SoC', 52),
  metric('sensor/GpuNvidia/2', 'GPU Memory Junction', 62), metric('sensor/GpuNvidia/2', 'GPU Core', 53.1),
  metric('sensor/Storage/3', 'Temperature #1', 47.9), metric('sensor/Storage/3', 'Composite Temperature', 45),
  metric('sensor/Storage/3', 'Warning Temperature', 89), metric('sensor/Storage/3', 'Critical Temperature', 94),
  metric('sensor/Storage/4', 'Composite Temperature', 37),
]

test('temperature groups keep separate GPUs and disks, and prefer core/composite readings over hotter secondary sensors', () => {
  const input = readings(), original = JSON.stringify(input), groups = temperatureGroups(input)
  assert.equal(groups.length, 5)
  assert.equal(groups.filter(group => group.category === 'gpu').length, 2)
  assert.equal(groups.filter(group => group.category === 'disk').length, 2)
  const gpu = groups.find(group => group.id === 'sensor/GpuNvidia/2')
  assert.equal(gpu.primary.name, 'GPU Core')
  assert.equal(gpu.primary.value, 53.1)
  assert.equal(gpu.readings.length, 2)
  assert.equal(groups.find(group => group.id === 'sensor/Storage/3').primary.value, 45)
  assert.equal(JSON.stringify(input), original)
  assert.deepEqual(Array.from(temperatureGroups([...input].reverse()), group => group.id), Array.from(groups, group => group.id))
})

test('SMART warning/critical thresholds remain identifiable but are excluded from current-temperature summaries', () => {
  const input = readings(), groups = temperatureGroups(input)
  const disk = groups.find(group => group.id === 'sensor/Storage/3')
  assert.equal(disk.readings.length, 2)
  assert.equal(disk.readings.some(item => item.value === 89 || item.value === 94), false)
  assert.equal(temperatureThreshold(input.find(item => item.name === 'Warning Temperature')), true)
  assert.equal(temperatureLabel(input.find(item => item.name === 'Warning Temperature')), '告警温度阈值')
  assert.equal(temperatureLabel(input.find(item => item.name === 'Critical Temperature')), '临界温度阈值')
})

test('missing drivers stay missing, and no readings create explicit unreported categories instead of invented zero values', () => {
  const cpu = temperatureGroups(readings()).find(group => group.category === 'cpu')
  assert.equal(temperatureAvailable(cpu.primary), false)
  assert.equal(cpu.primary.value, null)
  assert.match(temperatureReason(cpu), /PawnIO/)
  const empty = temperatureGroups([])
  assert.equal(empty.length, 3)
  assert.equal(empty.every(group => group.primary === null && group.readings.length === 0), true)
  assert.equal(empty.every(group => temperatureReason(group).includes('尚未上报')), true)
})

test('only finite available values are readings, including a legitimate zero, and an available sensor outranks an unavailable primary', () => {
  assert.equal(temperatureAvailable(metric('gpu', 'GPU Core', 0)), true)
  assert.equal(temperatureAvailable(metric('gpu', 'GPU Core', 55, 'failed')), false)
  assert.equal(temperatureAvailable(metric('gpu', 'GPU Core', Infinity)), false)
  assert.equal(temperatureAvailable(metric('gpu', 'GPU Core', null)), false)
  const groups = temperatureGroups([metric('sensor/Cpu/0', 'CPU Package', null, 'unknown'), metric('sensor/Cpu/0', 'Core Max', 50)])
  assert.equal(groups.find(group => group.category === 'cpu').primary.name, 'Core Max')
  assert.equal(temperatureLabel(metric('gpu', 'GPU Memory Junction', 62)), 'GPU 显存结温')
  assert.equal(temperatureLabel(metric('disk', 'Temperature #2', 47)), '温度传感器 2')
})

test('the actual temperature component renders stale-data context, driver diagnostics and measured values without threshold readings', async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, ws: false }, logLevel: 'error' })
  try {
    const component = await server.ssrLoadModule('/src/views/monitor/components/TemperaturePanel.vue')
    const html = await renderToString(vue.createSSRApp(component.default, { metrics: readings(), dataStatus: 'stale' }))
    assert.match(html, /硬件温度/)
    assert.match(html, /最后采样温度/)
    assert.match(html, /驱动缺失/)
    assert.match(html, /PawnIO/)
    assert.match(html, /GPU 核心温度/)
    assert.match(html, /53\.1/)
    assert.match(html, /GPU 显存结温/)
    assert.doesNotMatch(html, /Warning Temperature|Critical Temperature|告警温度阈值|临界温度阈值|NaN|Infinity/)
  } finally { await server.close() }
})
