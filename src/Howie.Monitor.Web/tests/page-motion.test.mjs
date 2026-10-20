import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'

const code = ts.transpileModule(await readFile(new URL('../src/composables/usePageMotion.ts', import.meta.url), 'utf8'),
  { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
async function harness(reduced = false) {
  const lifecycle = {}, listeners = new Map(), calls = [], contexts = []
  const root = vue.shallowRef({ isConnected: true }), page = vue.ref('overview'), scope = vue.effectScope()
  const gsap = { matchMedia: () => {
    const context = { reverted: false, add: (query, callback, element) => {
      assert.equal(query, '(prefers-reduced-motion: no-preference)'); assert.strictEqual(element, root.value)
      if (!reduced) callback()
    }, revert: () => { context.reverted = true } }
    contexts.push(context); return context
  }, fromTo: (element, from, to) => { calls.push({ element, from, to }) } }
  const module = { exports: {}, require: name => name === 'gsap' ? { gsap } : { ...vue,
    onMounted: callback => { lifecycle.mount = callback }, onDeactivated: callback => { lifecycle.deactivate = callback }, onUnmounted: callback => { lifecycle.unmount = callback } },
    document: { addEventListener: (name, callback) => listeners.set(name, callback), removeEventListener: name => listeners.delete(name) } }
  vm.runInNewContext(code, module)
  scope.run(() => module.exports.usePageMotion(root, () => page.value))
  const settle = async () => { await vue.nextTick(); await vue.nextTick() }
  lifecycle.mount(); await settle()
  return { root, page, calls, contexts, listeners, settle, deactivate: () => lifecycle.deactivate(), close: () => { lifecycle.unmount(); scope.stop() } }
}

test('page changes have one restrained transition and rapid changes replace the previous context', async () => {
  const h = await harness()
  try {
    assert.equal(h.calls.length, 1)
    const transition = h.calls[0]
    assert.ok(transition.from.opacity >= .85); assert.ok(transition.from.y <= 4)
    assert.ok(transition.to.duration <= .25); assert.equal(transition.to.y, 0)
    assert.equal(transition.to.stagger, undefined); assert.equal(transition.to.scale, undefined)
    h.page.value = 'history'; h.page.value = 'notifications'; await h.settle()
    assert.equal(h.calls.length, 2); assert.equal(h.contexts[0].reverted, true)
    assert.strictEqual(h.calls[1].element, h.root.value)
  } finally { h.close() }
  assert.equal(h.contexts.at(-1).reverted, true); assert.equal(h.listeners.size, 0)
})

test('reduced motion and keyboard navigation keep content immediately visible, while pointer navigation can transition', async () => {
  const reduced = await harness(true)
  try { reduced.page.value = 'hardware'; await reduced.settle(); assert.equal(reduced.calls.length, 0) }
  finally { reduced.close() }
  const h = await harness()
  try {
    h.listeners.get('keydown')(); h.page.value = 'history'; await h.settle()
    assert.equal(h.calls.length, 1); assert.equal(h.contexts[0].reverted, true)
    h.listeners.get('pointerdown')(); h.page.value = 'hardware'; await h.settle()
    assert.equal(h.calls.length, 2)
  } finally { h.close() }
})

test('deactivation and detachment cancel pending transitions without leaving animations on cached pages', async () => {
  const h = await harness()
  try {
    h.page.value = 'enrollment'; await vue.nextTick(); h.deactivate(); await h.settle()
    assert.equal(h.calls.length, 1); assert.equal(h.contexts[0].reverted, true)
    h.root.value = { isConnected: false }; h.page.value = 'notifications'; await h.settle()
    assert.equal(h.calls.length, 1)
  } finally { h.close() }
})
