import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'
const tick = () => new Promise(resolve => setImmediate(resolve))
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done }); return { promise, resolve } }
async function harness(overrides = {}) {
  let mounted, unmounted, timeout
  const deleted = [], outputs = [], feedback = [], sockets = [], target = vue.ref('first')
  class Socket {
    static OPEN = 1
    readyState = 1; bufferedAmount = 0; sent = []; closed = false
    constructor(url) { this.url = url; sockets.push(this) }
    send(value) { this.sent.push(JSON.parse(value)) }
    close() { this.closed = true; this.readyState = 3 }
    receive(frame) { this.onmessage?.({ data: JSON.stringify(frame) }) }
  }
  const api = { getTerminalStatus: async () => ({ available: true, account: target.value + '\\admin' }),
    createTerminalSession: async () => ({ sessionId: 'session-' + target.value }),
    closeTerminalSession: async (id, session) => { deleted.push([id, session]) }, ...overrides }
  const source = await readFile(new URL('../src/views/monitor/composables/useTerminal.ts', import.meta.url), 'utf8')
  const sandbox = { exports: {}, require: name => name === 'vue' ? { ...vue, onMounted: cb => { mounted = cb }, onUnmounted: cb => { unmounted = cb } }
    : name === '../../../api/terminal' ? api : { useToast: () => ({ error: value => feedback.push(value) }) },
    AbortController, URL, URLSearchParams, WebSocket: Socket, window: { location: { href: 'https://monitor.example/' } },
    setTimeout: cb => { timeout = cb; return 1 }, clearTimeout: () => { timeout = null }, setInterval: () => 1, clearInterval() {} }
  vm.runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText, sandbox)
  let resets = 0
  const terminal = sandbox.exports.useTerminal(() => target.value, (data, done) => outputs.push({ data, done }), () => { resets++ })
  return { terminal, target, sockets, deleted, outputs, feedback, resets: () => resets, start: async () => { mounted(); await tick() }, close: () => unmounted(), timeout: () => timeout?.() }
}
const ready = { type: 'ready', administrator: true, account: 'PC\\admin', userSid: 'S-1-5-21-1-2-3-1001' }
test('late session creation after computer switching is closed with its original computer and never attaches', async () => {
  const creation = deferred(); const h = await harness({ createTerminalSession: () => creation.promise })
  try {
    await h.start(); const pending = h.terminal.connect('password', 80, 24)
    h.target.value = 'second'; await tick(); creation.resolve({ sessionId: 'old-session' }); await pending
    assert.equal(h.sockets.length, 0); assert.deepEqual(h.deleted, [['first', 'old-session']]); assert.equal(h.terminal.phase.value, 'idle')
    assert.equal(h.terminal.status.value.account, 'second\\admin')
  } finally { h.close() }
})
test('input is accepted only after actual administrator ready, and renderer acks follow rendering', async () => {
  const h = await harness()
  try {
    await h.start(); await h.terminal.connect('password', 80, 24); const socket = h.sockets[0]
    assert.equal(h.terminal.input('before'), false); assert.equal(socket.sent.length, 0)
    socket.receive(ready); assert.equal(h.terminal.phase.value, 'ready')
    assert.equal(h.terminal.input('\u0003'), true); assert.equal(socket.sent[0].data, '\u0003')
    socket.receive({ type: 'output', data: 'PS C:\\Users\\admin>' }); assert.equal(socket.sent.length, 1)
    h.outputs[0].done(); assert.equal(socket.sent[1].type, 'ack')
    h.terminal.resize(120, 30); assert.equal(socket.sent[2].columns, 120)
    h.terminal.resize(900, 30); assert.equal(socket.sent.length, 3)
  } finally { h.close() }
})
test('computer switching ends the old socket, clears its screen and rejects late output and render callbacks', async () => {
  const h = await harness()
  try {
    await h.start(); await h.terminal.connect('password', 80, 24); const socket = h.sockets[0]; socket.receive(ready)
    const late = socket.onmessage; socket.receive({ type: 'output', data: 'private-output' })
    h.target.value = 'second'; await tick()
    assert.equal(socket.closed, true); assert.deepEqual(h.deleted, [['first', 'session-first']]); assert.equal(h.terminal.account.value, '')
    late({ data: JSON.stringify({ type: 'output', data: 'stale-output' }) }); h.outputs[0].done()
    assert.equal(h.outputs.length, 1); assert.equal(socket.sent.length, 0); assert.ok(h.resets() >= 2)
  } finally { h.close() }
})
test('disconnect never reconnects or replays input, and status polling preserves the reason', async () => {
  const h = await harness()
  try {
    await h.start(); await h.terminal.connect('password', 80, 24); const socket = h.sockets[0]; socket.receive(ready)
    socket.onclose({ reason: '授权账号已注销' }); await tick(); await h.terminal.refresh()
    assert.equal(h.terminal.phase.value, 'closed'); assert.equal(h.terminal.error.value, '授权账号已注销')
    assert.equal(h.terminal.input('queued'), false); assert.equal(h.sockets.length, 1)
  } finally { h.close() }
})
test('a forged non-admin ready closes the session and old events cannot restore it', async () => {
  const h = await harness()
  try {
    await h.start(); await h.terminal.connect('password', 80, 24); const socket = h.sockets[0], late = socket.onmessage
    socket.receive({ ...ready, administrator: false }); assert.equal(h.terminal.phase.value, 'error'); assert.equal(socket.closed, true)
    late({ data: JSON.stringify(ready) }); assert.equal(h.terminal.account.value, '')
    assert.equal(h.feedback.length, 1)
  } finally { h.close() }
})
test('leaving the page during connection closes late authorizations and emits no private feedback', async () => {
  const creation = deferred(); const h = await harness({ createTerminalSession: () => creation.promise })
  await h.start(); const pending = h.terminal.connect('password', 80, 24); h.close()
  creation.resolve({ sessionId: 'late' }); await pending
  assert.deepEqual(h.deleted, [['first', 'late']]); assert.equal(h.sockets.length, 0); assert.equal(h.feedback.length, 0)
})
test('long input preserves unicode boundaries and rejects oversized paste before sending any part', async () => {
  const h = await harness()
  try {
    await h.start(); await h.terminal.connect('password', 80, 24); const socket = h.sockets[0]; socket.receive(ready)
    const text = 'a'.repeat(4095) + '😀你好'
    assert.equal(h.terminal.input(text), true); assert.equal(socket.sent.map(value => value.data).join(''), text)
    assert.equal(socket.sent[0].data.length, 4095)
    const count = socket.sent.length; assert.equal(h.terminal.input('x'.repeat(65537)), false); assert.equal(socket.sent.length, count)
  } finally { h.close() }
})
