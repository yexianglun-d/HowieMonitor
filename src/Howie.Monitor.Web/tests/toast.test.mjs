import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'

async function harness() {
  let now = 0, sequence = 0
  const timers = new Map()
  const source = await readFile(new URL('../src/composables/useToast.ts', import.meta.url), 'utf8')
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  const sandbox = { exports: {}, require: () => vue, Date: { now: () => now },
    setTimeout: (callback, duration) => { const id = ++sequence; timers.set(id, { callback, at: now + duration }); return id },
    clearTimeout: id => timers.delete(id) }
  vm.runInNewContext(code, sandbox)
  function advance(duration) {
    const target = now + duration
    while (true) {
      const next = [...timers.entries()].sort((a, b) => a[1].at - b[1].at)[0]
      if (!next || next[1].at > target) break
      now = next[1].at; timers.delete(next[0]); next[1].callback()
    }
    now = target
  }
  return { toast: sandbox.exports.useToast(), advance, timers }
}

test('messages expire by severity while error feedback stays readable longer', async () => {
  const { toast, advance, timers } = await harness()
  toast.success('Saved'); toast.warning('Check settings'); toast.error('Failed')
  advance(2999); assert.equal(toast.messages.value.length, 3)
  advance(1); assert.equal(toast.messages.value.length, 2)
  advance(1500); assert.equal(toast.messages.value.length, 1)
  assert.equal(toast.messages.value[0].kind, 'error')
  advance(1500); assert.equal(toast.messages.value.length, 0)
  assert.equal(timers.size, 0)
})

test('repeating the same message groups it and restarts its dismissal timer', async () => {
  const { toast, advance, timers } = await harness()
  const id = toast.success('Copied')
  advance(2500)
  assert.equal(toast.success('  Copied  '), id)
  assert.equal(toast.messages.value.length, 1)
  assert.equal(toast.messages.value[0].count, 2)
  assert.equal(timers.size, 1)
  advance(2999); assert.equal(toast.messages.value.length, 1)
  advance(1); assert.equal(toast.messages.value.length, 0)
})

test('hover and keyboard focus pause independently and resume with the remaining time', async () => {
  const { toast, advance, timers } = await harness()
  const id = toast.info('Reading')
  advance(1000); toast.pause(id, 'hover'); toast.pause(id, 'focus')
  advance(10000); assert.equal(toast.messages.value.length, 1)
  toast.resume(id, 'hover'); assert.equal(timers.size, 0)
  advance(10000); assert.equal(toast.messages.value.length, 1)
  toast.resume(id, 'focus'); toast.resume(id, 'focus')
  assert.equal(timers.size, 1)
  advance(1999); assert.equal(toast.messages.value.length, 1)
  advance(1); assert.equal(toast.messages.value.length, 0)
})

test('a duplicate received while paused resets the duration without unpausing', async () => {
  const { toast, advance, timers } = await harness()
  const id = toast.success('Saved')
  advance(2000); toast.pause(id, 'hover'); toast.success('Saved')
  advance(10000); assert.equal(timers.size, 0)
  toast.resume(id, 'hover'); advance(2999)
  assert.equal(toast.messages.value.length, 1)
  advance(1); assert.equal(toast.messages.value.length, 0)
})

test('stack limits, manual dismissal and session cleanup cancel all removed timers', async () => {
  const { toast, advance, timers } = await harness()
  assert.equal(toast.info('  '), undefined)
  const first = toast.info('First')
  const second = toast.success('Second')
  toast.warning('Third'); toast.error('Fourth')
  assert.equal(toast.messages.value.length, 3)
  assert.equal(toast.messages.value.some(item => item.id === first), false)
  assert.equal(timers.size, 3)
  toast.dismiss(second); assert.equal(timers.size, 2)
  toast.clear(); assert.equal(timers.size, 0)
  advance(10000); assert.equal(toast.messages.value.length, 0)
})
