import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'
import { renderToString } from 'vue/server-renderer'
import { createServer } from 'vite'

const tick = () => new Promise(resolve => setImmediate(resolve))
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no }); return { promise, resolve, reject } }
const rule = { id: 'test-rule', name: '磁盘空间', enabled: true, kind: 'metric', key: 'volume.free', metricDeviceId: 'C:', unit: 'B', comparison: 'below', threshold: 10 * 1024 ** 3, recoveryThreshold: 11 * 1024 ** 3, durationSeconds: 60, recoverySeconds: 30, notifyRecovery: true }
const response = (id, rules = []) => ({ computerId: id, rules, events: [] })

async function harness(overrides = {}) {
  const id = vue.ref('first'), lifecycle = {}, feedback = [], saved = new Map(), payloads = []
  const persist = (computerId, rules) => { saved.set(computerId, structuredClone(rules)); return response(computerId, saved.get(computerId)) }
  const api = { getComputerNotifications: async computerId => response(computerId, saved.get(computerId) ?? []),
    saveComputerNotifications: async (computerId, rules) => { payloads.push({ computerId, rules }); return overrides.save ? overrides.save(computerId, rules, persist) : persist(computerId, rules) }, ...overrides.api }
  const source = await readFile(new URL('../src/views/monitor/composables/useComputerNotifications.ts', import.meta.url), 'utf8')
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  const scope = vue.effectScope(), sandbox = { exports: {}, require: name => name === 'vue' ? { ...vue, onMounted: fn => { lifecycle.mount = fn }, onUnmounted: fn => { lifecycle.unmount = fn }, onActivated: fn => { lifecycle.activate = fn }, onDeactivated: fn => { lifecycle.deactivate = fn } }
    : name === '../../../api/notifications' ? api : { useToast: () => Object.fromEntries(['success', 'error'].map(kind => [kind, message => feedback.push({ kind, message })])) },
    AbortController, structuredClone, Error, setTimeout: () => 1, clearTimeout() {}, window: { addEventListener() {}, removeEventListener() {} } }
  vm.runInNewContext(code, sandbox)
  const state = scope.run(() => sandbox.exports.useComputerNotifications(() => id.value)); lifecycle.mount(); await tick()
  return { id, state, lifecycle, feedback, payloads, close() { lifecycle.unmount(); scope.stop() } }
}

test('late rule loads cannot populate another computer even if abort is ignored', async () => {
  const old = deferred()
  const h = await harness({ api: { getComputerNotifications: id => id === 'first' ? old.promise : Promise.resolve(response(id)) } })
  try {
    h.id.value = 'second'; await tick()
    old.resolve(response('first', [rule])); await tick()
    assert.equal(h.state.current.value.ready, true)
    assert.equal(h.state.current.value.rules.length, 0)
    assert.equal(h.feedback.length, 0)
  } finally { h.close() }
})

test('drafts stay with their computer and byte thresholds are saved to the explicit computer', async () => {
  const h = await harness()
  try {
    h.state.current.value.rules.push({ ...rule }); h.id.value = 'second'; await tick()
    assert.equal(h.state.current.value.rules.length, 0)
    h.state.current.value.rules.push({ ...rule, threshold: 5 * 1024 ** 3 }); h.id.value = 'first'; await tick()
    assert.equal(h.state.current.value.rules[0].threshold, 10 * 1024 ** 3)
    await h.state.save(); await tick()
    assert.equal(h.payloads[0].computerId, 'first'); assert.equal(h.payloads[0].rules[0].unit, 'B')
    assert.equal(h.payloads[0].rules[0].threshold, 10737418240); assert.equal(h.state.dirty.value, false)
    h.id.value = 'second'; await tick()
    assert.equal(h.state.current.value.rules[0].threshold, 5 * 1024 ** 3)
    assert.equal(h.state.dirty.value, true)
    h.state.current.value.rules.splice(0); await h.state.save(); await tick()
    assert.equal(h.payloads[1].computerId, 'second'); assert.equal(h.payloads[1].rules.length, 0)
  } finally { h.close() }
})

test('saving first while viewing second cannot overwrite the second draft or show a misleading success toast', async () => {
  const pending = deferred()
  const h = await harness({ save: async (id, rules, persist) => { await pending.promise; return persist(id, rules) } })
  try {
    h.state.current.value.rules.push({ ...rule }); const saving = h.state.save()
    h.id.value = 'second'; await tick(); h.state.current.value.rules.push({ ...rule, name: 'second-only' })
    pending.resolve(); await saving; await tick()
    assert.equal(h.state.current.value.rules[0].name, 'second-only'); assert.equal(h.state.dirty.value, true)
    assert.equal(h.feedback.length, 0)
    h.id.value = 'first'; await tick()
    assert.equal(h.state.current.value.rules[0].name, rule.name); assert.equal(h.state.dirty.value, false)
  } finally { h.close() }
})

test('failed saves keep the owning draft and error when returning from another computer', async () => {
  const pending = deferred()
  const h = await harness({ save: () => pending.promise })
  try {
    h.state.current.value.rules.push({ ...rule }); const saving = h.state.save()
    h.id.value = 'second'; await tick(); pending.reject(new Error('storage unavailable')); await saving
    assert.equal(h.state.current.value.rules.length, 0); assert.equal(h.feedback.length, 0)
    h.id.value = 'first'; await tick()
    assert.equal(h.state.current.value.saveError, 'storage unavailable'); assert.equal(h.state.dirty.value, true)
  } finally { h.close() }
})

test('responses with wrong computer identity or foreign events are rejected before exposing data', async () => {
  for (const value of [response('second', [rule]), { ...response('first'), events: [{ computerId: 'second' }] }]) {
    const h = await harness({ api: { getComputerNotifications: async () => value } })
    try { assert.equal(h.state.current.value.ready, false); assert.equal(h.state.current.value.rules.length, 0); assert.equal(h.state.current.value.events.length, 0); assert.equal(h.feedback[0].kind, 'error') }
    finally { h.close() }
  }
})

test('logout disposal rejects late saves and emits no private feedback', async () => {
  const pending = deferred(), h = await harness({ save: () => pending.promise })
  h.state.current.value.rules.push({ ...rule }); const saving = h.state.save(); h.close()
  pending.resolve(response('first', [rule])); await saving
  assert.equal(h.feedback.length, 0)
})

test('the real fleet cards keep identical sensor identities partitioned by computer', async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, ws: false }, logLevel: 'error' })
  try {
    const component = await server.ssrLoadModule('/src/views/monitor/ComputersView.vue')
    const computers = ['first', 'second'].map((id, index) => ({ deviceId: id, name: '同名电脑', connectionStatus: 'online', dataStatus: 'fresh', sampledAt: '2026-10-20T00:00:00Z', lastHeartbeat: null,
      cpuPercent: index ? 75 : 15, memoryPercent: 40, temperatures: [{ key: 'temp', deviceId: 'sensor/Cpu/0', unit: '°C', name: 'CPU Package', value: index ? 85 : 45, status: 'available', source: 'test', reason: null }] }))
    const html = await renderToString(vue.createSSRApp(component.default, { computers }))
    const first = html.slice(html.indexOf('data-computer-id="first"'), html.indexOf('data-computer-id="second"'))
    const second = html.slice(html.indexOf('data-computer-id="second"'))
    assert.match(first, /15 %/); assert.match(first, /45 °C/); assert.doesNotMatch(first, /75 %|85 °C/)
    assert.match(second, /75 %/); assert.match(second, /85 °C/); assert.doesNotMatch(second, /15 %|45 °C/)
  } finally { await server.close() }
})

test('computer snapshots age using server time and never keep expired current readings', async () => {
  const source = await readFile(new URL('../src/utils/computers.ts', import.meta.url), 'utf8')
  const sandbox = { exports: {}, Date, Math }
  vm.runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, sandbox)
  const computer = { deviceId: 'first', dataStatus: 'fresh', connectionStatus: 'online', serverNow: '2026-10-20T00:00:08Z', sampledAt: '2026-10-20T00:00:00Z', lastHeartbeat: '2026-10-20T00:00:08Z', cpuPercent: 15, temperatures: [{ value: 45 }] }
  assert.equal(sandbox.exports.ageComputerSnapshot(computer, 0).cpuPercent, 15)
  assert.equal(sandbox.exports.ageComputerSnapshot(computer, 3000).dataStatus, 'delayed')
  const old = sandbox.exports.ageComputerSnapshot(computer, 31000)
  assert.equal(old.dataStatus, 'stale'); assert.equal(old.connectionStatus, 'disconnected'); assert.equal(old.cpuPercent, null); assert.equal(old.temperatures.length, 0)
  assert.equal(computer.cpuPercent, 15)
})
