import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'
import { renderToString } from 'vue/server-renderer'
import { createServer } from 'vite'

const tick = () => new Promise(resolve => setImmediate(resolve))
const computers = ['first', 'second'].map(deviceId => ({ deviceId, name: deviceId, connectionStatus: 'online', dataStatus: 'fresh' }))
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done }); return { promise, resolve } }
const latest = id => ({ sample: { deviceId: id, sampledAt: new Date().toISOString(), metrics: [{ key: 'cpu.usage', deviceId: 'cpu/total', unit: '%', value: id === 'first' ? 10 : 80 }], processes: [] }, serverNow: new Date().toISOString(), dataStatus: 'fresh', connectionStatus: 'online', lastHeartbeat: new Date().toISOString() })

async function harness(overrides = {}) {
  let mounted, unmounted
  const historyCalls = []
  const api = { getSession: async () => ({ authenticated: true, setupRequired: false }), getComputers: async () => computers,
    getDevice: async id => ({ deviceId: id, name: id }), getLatest: async id => latest(id),
    getHistory: async id => { historyCalls.push(id); return [{ sampledAt: new Date().toISOString(), value: id === 'first' ? 10 : 80 }] },
    logout: async () => {}, ...overrides }
  const source = await readFile(new URL('../src/composables/useMonitor.ts', import.meta.url), 'utf8')
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  const sandbox = { exports: {}, require: name => name === 'vue' ? { ...vue, onMounted: callback => { mounted = callback }, onUnmounted: callback => { unmounted = callback } }
    : name === '../api/monitor' ? api : { ApiError: class ApiError extends Error {} },
    AbortController, DOMException, Date, setTimeout: () => 1, clearTimeout() {}, setInterval: () => 1, clearInterval() {},
    document: { hidden: false, addEventListener() {}, removeEventListener() {} } }
  vm.runInNewContext(code, sandbox)
  const monitor = sandbox.exports.useMonitor()
  return { monitor, historyCalls, start: () => mounted(), close: () => unmounted() }
}

test('switching clears the previous computer and rejects a late response even when abort is ignored', async () => {
  const old = deferred()
  const h = await harness({ getDevice: id => id === 'first' ? old.promise : Promise.resolve({ deviceId: id, name: id }) })
  try {
    const initial = h.start(); await tick()
    assert.equal(h.monitor.selectedComputerId.value, 'first')
    h.monitor.selectComputer('second')
    assert.equal(h.monitor.device.value, null); assert.equal(h.monitor.latest.value, null)
    await tick()
    assert.equal(h.monitor.device.value.deviceId, 'second')
    assert.equal(h.monitor.latest.value.sample.metrics[0].value, 80)
    old.resolve({ deviceId: 'first', name: 'first' }); await initial; await tick()
    assert.equal(h.monitor.device.value.deviceId, 'second')
    assert.deepEqual(h.historyCalls, ['second'])
  } finally { h.close() }
})

test('logout prevents an in-flight computer response from restoring private telemetry', async () => {
  const old = deferred()
  const h = await harness({ getDevice: () => old.promise })
  try {
    const initial = h.start(); await tick(); await h.monitor.signOut()
    old.resolve({ deviceId: 'first', name: 'first' }); await initial
    assert.equal(h.monitor.authenticated.value, false)
    assert.equal(h.monitor.device.value, null); assert.equal(h.monitor.latest.value, null)
    assert.equal(h.monitor.computers.value.length, 0); assert.equal(h.historyCalls.length, 0)
  } finally { h.close() }
})

test('the real computer selector renders distinct accessible choices and independent status', async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false }, logLevel: 'error' })
  try {
    const component = await server.ssrLoadModule('/src/views/monitor/components/ComputerSelector.vue')
    const html = await renderToString(vue.createSSRApp(component.default, { computers: computers.map((item, index) => ({ ...item,
      name: '同名电脑', connectionStatus: index ? 'disconnected' : 'online', cpuPercent: index ? null : 10, memoryPercent: index ? null : 40 })), selectedId: 'second' }))
    assert.equal((html.match(/<button /g) ?? []).length, 2)
    assert.equal((html.match(/aria-pressed="true"/g) ?? []).length, 1)
    assert.match(html, /在线/); assert.match(html, /离线/); assert.match(html, /first/); assert.match(html, /second/)
  } finally { await server.close() }
})
