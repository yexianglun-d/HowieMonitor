import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import * as vue from 'vue'
const compile = async name => ts.transpileModule(await readFile(new URL(`../src/composables/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText
const scrollCode = await compile('useScrollExperience'), railCode = await compile('useHorizontalRail')
const node = (label, top = 0) => ({ dataset: { scrollSection: label }, isConnected: true, getClientRects: () => [1], getBoundingClientRect: () => ({ top }), classList: { add() {}, remove() {}, toggle() {} }, focus(options) { this.focused = options } })
async function harness(reduced = false) {
  const lifecycle = {}, frames = new Map(), observers = [], resizes = [], triggers = [], contexts = [], media = [], scrolls = [], tweens = []
  let frameId = 0, refreshes = 0, currentContext, nodes = [node('实时指标', 300), node('性能变化', 900)]
  const page = vue.shallowRef({ querySelector: () => ({}) }), content = vue.shallowRef({ isConnected: true, querySelectorAll: () => nodes }), toolbar = vue.shallowRef({ offsetHeight: 100, querySelector: () => ({}) }), route = vue.ref('overview')
  const gsap = { registerPlugin() {}, set() {}, to: (...args) => tweens.push(args), context(callback) { const context = { items: [], reverted: false, revert() { this.reverted = true; this.items.forEach(item => { item.killed = true }) } }; contexts.push(context); currentContext = context; callback(); currentContext = undefined; return context }, matchMedia() { const item = { reverted: false, add(query, callback) { if (!reduced) callback() }, revert() { this.reverted = true } }; media.push(item); return item } }
  const ScrollTrigger = { update() {}, create(options) { const item = { ...options, start: triggers.length * 500, progress: 0, scroll: () => 0 }; triggers.push(item); currentContext?.items.push(item); return item }, refresh() { refreshes++; triggers.filter(item => !item.killed).forEach(item => item.onRefresh?.(item)) } }
  const observerClass = list => class { constructor(callback) { this.callback = callback; list.push(this) } observe() {} disconnect() { this.disconnected = true } }
  const scope = vue.effectScope(), module = { exports: {}, require: name => name === 'gsap' ? { gsap } : name === 'gsap/ScrollTrigger' ? { ScrollTrigger } : { ...vue, onMounted: callback => { lifecycle.mount = callback }, onUnmounted: callback => { lifecycle.unmount = callback } }, MutationObserver: observerClass(observers), ResizeObserver: observerClass(resizes), getComputedStyle: () => ({ top: '20px' }), requestAnimationFrame: callback => { frames.set(++frameId, callback); return frameId }, cancelAnimationFrame: id => frames.delete(id), window: { scrollY: 200, scrollTo: options => scrolls.push(options), matchMedia: () => ({ matches: reduced }) } }
  vm.runInNewContext(scrollCode, module)
  const api = scope.run(() => module.exports.useScrollExperience(page, content, toolbar, () => route.value))
  const settle = async () => { await vue.nextTick(); await new Promise(setImmediate); const pending = [...frames.values()]; frames.clear(); pending.forEach(callback => callback()); await vue.nextTick() }
  lifecycle.mount(); await settle()
  return { api, route, nodes: () => nodes, replace: value => { nodes = value }, observers, resizes, contexts, media, triggers, scrolls, tweens, frames, settle, refreshes: () => refreshes, close: () => { lifecycle.unmount(); scope.stop() } }
}
test('live readings and replaced process rows leave scroll motion intact; layout changes refresh it', async () => {
  const h = await harness()
  try {
    const refreshes = h.refreshes(), count = h.contexts.length
    h.observers.at(-1).callback([{ addedNodes: [{ nodeType: 1 }], removedNodes: [] }]); await h.settle()
    assert.equal(h.contexts.length, count); assert.equal(h.refreshes(), refreshes)
    h.resizes.at(-1).callback(); await h.settle(); assert.equal(h.contexts.length, count); assert.equal(h.refreshes(), refreshes + 1)
    h.replace([node('另一组', 300)]); h.observers.at(-1).callback([{ addedNodes: [{ nodeType: 1 }], removedNodes: [] }]); await h.settle()
    assert.equal(h.contexts.length, count + 1); assert.equal(h.contexts[0].reverted, true); assert.equal(h.api.sections.value[0].label, '另一组')
  } finally { h.close() }
})
test('rapid route changes and disposal release their own triggers, observers and pending work', async () => {
  const h = await harness(); h.route.value = 'detail'; h.route.value = 'notifications'; await h.settle()
  assert.equal(h.contexts.length, 2); assert.equal(h.contexts[0].reverted, true); assert.equal(h.observers[0].disconnected, true); assert.equal(h.media[0].reverted, true)
  h.route.value = 'hardware'; await vue.nextTick(); h.close(); await h.settle()
  assert.ok(h.contexts.every(item => item.reverted)); assert.ok(h.triggers.every(item => item.killed)); assert.ok(h.observers.every(item => item.disconnected)); assert.ok(h.resizes.every(item => item.disconnected)); assert.equal(h.frames.size, 0)
})
test('chapter navigation accounts for sticky bars; keyboard and reduced motion use instant focused navigation', async () => {
  const h = await harness()
  try { h.api.go('section-1', true); assert.equal(h.scrolls.at(-1).top, 962); assert.equal(h.scrolls.at(-1).behavior, 'instant'); assert.equal(h.nodes()[1].focused.preventScroll, true); h.api.go('section-0'); assert.equal(h.scrolls.at(-1).behavior, 'smooth') } finally { h.close() }
  const reduced = await harness(true)
  try { assert.equal(reduced.tweens.length, 0); reduced.api.go('section-1'); assert.equal(reduced.scrolls.at(-1).behavior, 'instant') } finally { reduced.close() }
})
async function railHarness(reduced = false, mount = true) {
  const lifecycle = {}, resizes = [], calls = [], listeners = new Map(), track = vue.shallowRef({ scrollWidth: 1200, clientWidth: 400, scrollLeft: 0, firstElementChild: { offsetWidth: 300 }, children: [], scrollBy: options => calls.push(options), addEventListener: (event, callback) => listeners.set(event, callback), removeEventListener: event => listeners.delete(event) })
  const scope = vue.effectScope(), module = { exports: {}, require: () => ({ ...vue, onMounted: callback => { lifecycle.mount = callback }, onUnmounted: callback => { lifecycle.unmount = callback } }), ResizeObserver: class { constructor(callback) { this.callback = callback; resizes.push(this) } observe() {} disconnect() { this.disconnected = true } }, getComputedStyle: () => ({ columnGap: '10px' }), window: { matchMedia: () => ({ matches: reduced }) } }
  vm.runInNewContext(railCode, module); const api = scope.run(() => module.exports.useHorizontalRail(track, () => 'a'))
  const settle = async () => { await vue.nextTick(); await vue.nextTick() }; if (mount) { lifecycle.mount(); await settle() }
  return { api, track, calls, listeners, resizes, settle, mount: () => lifecycle.mount(), close: () => { lifecycle.unmount(); scope.stop() } }
}
test('horizontal browsing uses actual card spacing without stealing keys from nested controls', async () => {
  const h = await railHarness()
  try {
    assert.equal(h.api.before.value, false); assert.equal(h.api.after.value, true); h.api.move(1); assert.equal(h.calls[0].left, 310); assert.equal(h.calls[0].behavior, 'smooth')
    const target = {}, event = { key: 'ArrowRight', target, currentTarget: target, preventDefault() { this.prevented = true } }; h.api.keydown(event); assert.equal(h.calls.at(-1).behavior, 'instant'); assert.equal(event.prevented, true)
    h.api.keydown({ ...event, target: {} }); assert.equal(h.calls.length, 2); h.track.value.scrollLeft = 800; h.listeners.get('scroll')(); assert.equal(h.api.after.value, false); assert.equal(h.api.progress.value, 1)
  } finally { h.close() }
  assert.equal(h.listeners.size, 0); assert.ok(h.resizes.every(item => item.disconnected))
  const reduced = await railHarness(true); try { reduced.api.move(1); assert.equal(reduced.calls[0].behavior, 'instant') } finally { reduced.close() }
})
test('a horizontal rail unmounted during nextTick never attaches listeners afterward', async () => {
  const h = await railHarness(false, false); h.mount(); h.close(); await h.settle(); assert.equal(h.listeners.size, 0); assert.equal(h.resizes.length, 0)
})
