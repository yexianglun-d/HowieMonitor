import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'
import { renderToString } from 'vue/server-renderer'
import { createServer } from 'vite'

const sandbox = { exports: {} }
vm.runInNewContext(ts.transpileModule(await readFile(new URL('../src/utils/gpus.ts', import.meta.url), 'utf8'),
  { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText, sandbox)
const { gpuSummaries, gpuUsageAvailable, gpuUsageLabel } = sandbox.exports
const metric = (deviceId, name, value, status = 'available', unit = '%', hardware = deviceId.includes('Amd') ? 'AMD Radeon(TM) Graphics' : 'NVIDIA GeForce RTX 5070 Ti') => ({
  deviceId, key: deviceId + '/' + name, name, value, status, unit, reason: null,
  source: `LibreHardwareMonitorLib 0.9.6: ${hardware}/${unit === '%' ? 'Load' : 'Temperature'}`,
})
const readings = () => [
  metric('sensor/GpuAmd/1', 'D3D 3D', 0), metric('sensor/GpuAmd/1', 'GPU Core', 0),
  metric('sensor/GpuNvidia/2', 'D3D 3D', 5.17), metric('sensor/GpuNvidia/2', 'GPU Core', 5),
  metric('sensor/GpuNvidia/2', 'GPU Bus', 95), metric('sensor/GpuNvidia/2', 'GPU Memory', 80),
]

test('idle iGPU and busy dGPU are separate regardless of input order and keep original identities', () => {
  const input = readings(), before = JSON.stringify(input)
  for (const values of [input, [...input].reverse()]) {
    const gpus = gpuSummaries(values)
    assert.equal(gpus.length, 2)
    assert.equal(gpus[0].name, 'AMD Radeon(TM) Graphics'); assert.equal(gpus[0].usage.value, 0)
    assert.equal(gpus[1].name, 'NVIDIA GeForce RTX 5070 Ti'); assert.equal(gpus[1].usage.value, 5)
    assert.strictEqual(gpus[1].usage, input.find(item => item.deviceId === 'sensor/GpuNvidia/2' && item.name === 'GPU Core'))
  }
  assert.equal(JSON.stringify(input), before)
})

test('identical GPU models stay separate, and GPUs without usage keep missing values instead of taking another GPUs reading', () => {
  const gpus = gpuSummaries([metric('sensor/GpuNvidia/1', 'GPU Core', 0), metric('sensor/GpuNvidia/2', 'GPU Core', 70),
    metric('sensor/GpuIntel/3', 'GPU Core', 50, 'available', '°C', 'Intel Graphics')])
  assert.equal(gpus.length, 3)
  assert.equal(gpus.find(gpu => gpu.deviceId === 'sensor/GpuIntel/3').usage, null)
  assert.equal(gpus.filter(gpu => gpu.name === 'NVIDIA GeForce RTX 5070 Ti').length, 2)
})

test('an available D3D reading can replace an unavailable core reading with an accurate label, while valid zero stays zero', () => {
  const core = metric('sensor/GpuIntel/1', 'GPU Core', null, 'unknown')
  const d3d = metric(core.deviceId, 'D3D 3D', 28)
  const gpu = gpuSummaries([core, d3d])[0]
  assert.strictEqual(gpu.usage, d3d); assert.equal(gpuUsageLabel(gpu.usage), '3D 引擎占用')
  assert.equal(gpuSummaries([{ ...core, value: 0, status: 'available' }, d3d])[0].usage.value, 0)
  for (const value of [null, NaN, Infinity, -1, 101]) assert.equal(gpuUsageAvailable({ ...core, value, status: 'available' }), false)
  assert.equal(gpuUsageAvailable({ ...core, value: 0, status: 'available' }), true)
  assert.equal(gpuUsageAvailable({ ...core, value: 50 }), false)
  assert.equal(gpuSummaries([]).length, 0)
})

test('actual cards render each hardware name and its own history, and fleet summaries have no ambiguous aggregate', async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, ws: false }, logLevel: 'error' })
  try {
    const card = (await server.ssrLoadModule('/src/views/monitor/components/MetricCard.vue')).default
    const groups = gpuSummaries(readings()), curves = []
    for (const gpu of groups) {
      const html = await renderToString(vue.createSSRApp(card, { title: 'GPU', code: '02', hardware: gpu.name,
        subtitle: gpuUsageLabel(gpu.usage), metric: gpu.usage, color: '#aeb7f1',
        points: [gpu.usage.value, gpu.usage.value + 1].map((value, index) => ({ sampledAt: new Date(1700000000000 + index * 2000).toISOString(), value })) }))
      assert.ok(html.includes(gpu.name)); assert.ok(html.includes(`metric-number">${gpu.usage.value}<small>`))
      curves.push([...html.matchAll(/<path d="([^"]+)" fill="none" stroke="#aeb7f1"/g)].map(match => match[1]).join())
    }
    assert.notEqual(curves[0], curves[1]); assert.ok(curves.every(Boolean))
    const fleet = (await server.ssrLoadModule('/src/views/monitor/ComputersView.vue')).default
    const html = await renderToString(vue.createSSRApp(fleet, { computers: [{ deviceId: 'first', name: 'Test PC', connectionStatus: 'online', dataStatus: 'fresh',
      cpuPercent: 10, memoryPercent: 40, gpuPercent: 99, gpus: groups }] }))
    assert.match(html, /AMD Radeon\(TM\) Graphics/); assert.match(html, /NVIDIA GeForce RTX 5070 Ti/)
    assert.match(html, /<dd[^>]*>0 %<\/dd>/); assert.match(html, /<dd[^>]*>5 %<\/dd>/)
    assert.doesNotMatch(html, /99 %/)
  } finally { await server.close() }
})

test('aging a computer snapshot preserves GPU names but removes live usage without mutating the snapshot', async () => {
  const utility = { exports: {} }
  vm.runInNewContext(ts.transpileModule(await readFile(new URL('../src/utils/computers.ts', import.meta.url), 'utf8'),
    { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, { ...utility, Date, Math })
  const now = new Date().toISOString(), gpus = gpuSummaries(readings())
  const input = { dataStatus: 'fresh', connectionStatus: 'online', sampledAt: now, lastHeartbeat: now, serverNow: now, gpus }
  const aged = utility.exports.ageComputerSnapshot(input, 31000)
  assert.equal(aged.dataStatus, 'stale'); assert.equal(aged.gpus.length, 2)
  assert.equal(aged.gpus.every(gpu => gpu.usage === null), true)
  assert.equal(aged.gpus[1].name, gpus[1].name); assert.equal(gpus[1].usage.value, 5)
})
