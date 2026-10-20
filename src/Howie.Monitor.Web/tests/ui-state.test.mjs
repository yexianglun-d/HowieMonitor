import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'
import { renderToString } from 'vue/server-renderer'
import { createServer } from 'vite'

const tick = () => new Promise(resolve => setImmediate(resolve))
async function moduleAt(path, dependencies = {}, globals = {}) {
  const source = await readFile(new URL('../src/' + path, import.meta.url), 'utf8')
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  const sandbox = { exports: {}, require: name => dependencies[name], URLSearchParams, Error, ...globals }
  vm.runInNewContext(code, sandbox)
  return sandbox.exports
}
const rule = { id: 'storage', kind: 'metric', key: 'volume.free', metricDeviceId: 'C:', unit: 'B', name: '可用空间', enabled: true,
  threshold: 10 * 1024 ** 3, recoveryThreshold: 11 * 1024 ** 3, durationSeconds: 60, recoverySeconds: 30, notifyRecovery: true, comparison: 'below' }
const response = () => ({ settings: { enabled: false, dailyLimit: 5, hasCredential: false, rules: [] }, events: [], requestsToday: 0, budgetDate: '2026-10-19' })

async function notificationHarness(overrides = {}) {
  const lifecycle = {}, listeners = new Map(), timers = [], feedback = []
  let payload
  const api = { getNotifications: async () => response(), saveNotifications: async value => { payload = value; return { ...response(), settings: { ...value, hasCredential: !!value.sendKey } } }, ...overrides }
  const mod = await moduleAt('views/monitor/composables/useNotifications.ts', {
    vue: { ...vue, onMounted: fn => { lifecycle.mount = fn }, onUnmounted: fn => { lifecycle.unmount = fn },
      onActivated: fn => { lifecycle.activate = fn }, onDeactivated: fn => { lifecycle.deactivate = fn } },
    '../../../api/notifications': api,
    '../../../composables/useToast': { useToast: () => Object.fromEntries(['success', 'error', 'info'].map(kind => [kind, message => feedback.push({ kind, message })])) },
  }, { AbortController, structuredClone, setTimeout: fn => { timers.push(fn); return timers.length }, clearTimeout() {},
    window: { addEventListener: (key, fn) => listeners.set(key, fn), removeEventListener: key => listeners.delete(key) } })
  const state = mod.useNotifications()
  lifecycle.mount(); await tick()
  return { state, lifecycle, timers, listeners, feedback, payload: () => payload }
}

test('notification draft and transient key survive page deactivation and refresh, and are cleared on logout disposal', async () => {
  const h = await notificationHarness()
  h.state.settings.value.dailyLimit = 9; h.state.sendKey.value = 'unsaved-secret'
  assert.equal(h.state.dirty.value, true)
  h.lifecycle.deactivate(); h.lifecycle.activate(); await tick(); await h.state.refresh()
  assert.equal(h.state.settings.value.dailyLimit, 9)
  assert.equal(h.state.sendKey.value, 'unsaved-secret')
  let warned = false
  h.listeners.get('beforeunload')({ preventDefault() { warned = true }, returnValue: undefined })
  assert.equal(warned, true)
  h.lifecycle.unmount()
  assert.equal(h.state.sendKey.value, '')
  assert.equal(h.listeners.size, 0)
})

test('saving the global channel never includes computer rules and resets the saved-state indicator', async () => {
  const h = await notificationHarness()
  try {
    h.state.settings.value.rules.push({ ...rule }); h.state.settings.value.dailyLimit = 7
    await h.state.save()
    assert.equal(h.payload().rules.length, 0)
    assert.equal(h.state.dirty.value, false)
    assert.deepEqual(h.feedback, [{ kind: 'success', message: '通知设置已保存' }])
    let warned = false
    h.listeners.get('beforeunload')({ preventDefault() { warned = true } })
    assert.equal(warned, false)
  } finally { h.lifecycle.unmount() }
})

test('late notification requests cannot restore data after disposal', async () => {
  let resolve
  const h = await notificationHarness({ getNotifications: () => new Promise(done => { resolve = done }) })
  h.lifecycle.unmount(); resolve(response()); await tick()
  assert.equal(h.state.ready.value, false)
  assert.equal(h.timers.length, 0)
  assert.equal(h.feedback.length, 0)
})

test('a failed save keeps the draft and reports the actual failure through toast', async () => {
  const h = await notificationHarness({ saveNotifications: async () => { throw new Error('Save unavailable') } })
  try {
    h.state.settings.value.dailyLimit = 8
    await h.state.save()
    assert.equal(h.state.dirty.value, true)
    assert.equal(h.state.settings.value.dailyLimit, 8)
    assert.equal(h.state.busy.value, false)
    assert.deepEqual(h.feedback, [{ kind: 'error', message: 'Save unavailable' }])
  } finally { h.lifecycle.unmount() }
})

test('polling errors are reported once per outage, and an explicit retry can report again', async () => {
  let failing = true
  const h = await notificationHarness({ getNotifications: async () => { if (failing) throw new Error('Read unavailable'); return response() } })
  try {
    assert.equal(h.feedback.length, 1)
    await h.timers.at(-1)()
    assert.equal(h.feedback.length, 1)
    await h.state.refresh()
    assert.equal(h.feedback.length, 2)
    failing = false; await h.timers.at(-1)()
    assert.equal(h.state.error.value, '')
    failing = true; await h.timers.at(-1)()
    assert.equal(h.feedback.length, 3)
  } finally { h.lifecycle.unmount() }
})

test('a save finishing after logout cannot emit a toast', async () => {
  let resolve
  const h = await notificationHarness({ saveNotifications: () => new Promise(done => { resolve = done }) })
  h.state.settings.value.dailyLimit = 8
  const saving = h.state.save()
  h.lifecycle.unmount(); resolve(response()); await saving
  assert.equal(h.feedback.length, 0)
})

test('a queued test notification reports queueing rather than delivery', async () => {
  const h = await notificationHarness({ testNotifications: async () => response() })
  try {
    await h.state.test()
    assert.deepEqual(h.feedback, [{ kind: 'info', message: '测试通知已排队，请查看发送记录' }])
  } finally { h.lifecycle.unmount() }
})

test('readable rule units round-trip without changing API units or treating empty input as zero', async () => {
  const units = await moduleAt('utils/notificationUnits.ts')
  for (const [unit, label, value] of [['B', 'GiB', 10 * 1024 ** 3], ['B/s', 'MiB/s', 4.5 * 1024 ** 2], ['%', '%', 92.5], ['°C', '°C', 80]]) {
    assert.equal(units.ruleUnit(unit), label)
    assert.equal(units.ruleValue(String(units.ruleInput(value, unit)), unit), value)
  }
  assert.equal(Number.isNaN(units.ruleValue('', 'B')), true)
  assert.equal(units.ruleValue('0', 'B'), 0)
})

test('page and historical query links round-trip encoded device and metric identities', async () => {
  const location = await moduleAt('utils/location.ts')
  const query = new URLSearchParams({ computer: 'desk&one', historyComputer: 'old/computer', metric: JSON.stringify(['C:', 'volume.free', 'B']), hours: '168' })
  const hash = location.locationHash('history', query)
  const result = location.readLocation(hash)
  assert.equal(result.page, 'history')
  assert.equal(result.query.get('computer'), 'desk&one')
  assert.equal(result.query.get('metric'), JSON.stringify(['C:', 'volume.free', 'B']))
  assert.equal(result.query.get('hours'), '168')
  assert.equal(location.readLocation('#unknown').page, 'computers')
  assert.equal(location.readLocation('').page, 'computers')
})

test('chart keeps missing samples disconnected and uses rate axes for byte-per-second metrics', async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, ws: false }, logLevel: 'error' })
  try {
    const component = await server.ssrLoadModule('/src/components/base/TrendChart.vue')
    const points = [10 * 1024 ** 2, null, 0, 20 * 1024 ** 2].map((value, index) => ({ sampledAt: new Date(1700000000000 + index * 2000).toISOString(), value }))
    const html = await renderToString(vue.createSSRApp(component.default, { points, percent: false, unit: 'B/s' }))
    assert.match(html, /MiB\/s/)
    assert.doesNotMatch(html, /100 %|NaN|Infinity/)
    const lines = [...html.matchAll(/<path d="([^"]+)" fill="none" stroke="#9adbc9"/g)].map(match => match[1])
    assert.equal(lines.length, 2)
    assert.match(lines[1], /^M[\d.]+,156.0 L/)
    assert.match(html, /方向键/)
  } finally { await server.close() }
})
