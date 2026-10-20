import { nextTick, onMounted, onUnmounted, ref, watch, type Ref } from 'vue'
import { gsap } from 'gsap'
import { ScrollTrigger } from 'gsap/ScrollTrigger'

gsap.registerPlugin(ScrollTrigger)
export interface ScrollSection { id: string; label: string }

export function useScrollExperience(page: Ref<HTMLElement | null>, content: Ref<HTMLElement | null>, toolbar: Ref<HTMLElement | null>, change: () => string) {
  const sections = ref<ScrollSection[]>([]), active = ref(''), stuck = ref(false)
  let context: gsap.Context | undefined, media: gsap.MatchMedia | undefined
  let observer: MutationObserver | undefined, resize: ResizeObserver | undefined, frame = 0, revision = 0
  let elements: HTMLElement[] = [], disposed = false, connected = false, layoutChanged = false
  const offset = () => {
    const bar = toolbar.value
    return bar && bar.offsetHeight ? (parseFloat(getComputedStyle(bar).top) || 0) + bar.offsetHeight + 18 : 24
  }
  function stop() { context?.revert(); media?.revert(); context = undefined; media = undefined }
  function rebuild() {
    frame = 0
    const root = content.value, bar = toolbar.value
    if (!root?.isConnected || !page.value) return
    const next = [...root.querySelectorAll<HTMLElement>('[data-scroll-section]')].filter(el => el.getClientRects().length)
    if (connected && next.length === elements.length && next.every((el, i) => el === elements[i])) { if (layoutChanged) ScrollTrigger.refresh(true); layoutChanged = false; return }
    connected = true; stop(); elements.forEach(el => el.classList.remove('is-reading')); elements = next
    sections.value = next.map((el, index) => ({ id: `section-${index}`, label: el.dataset.scrollSection || '' }))
    active.value = sections.value[0]?.id ?? ''
    // 只跟踪段落身份和布局；每两秒的读数/曲线更新不会重播或重建动效。
    context = gsap.context(() => {
      let line: HTMLElement | null = null, currentIndex = -1
      const update = (self: ScrollTrigger) => {
        line ??= bar?.querySelector<HTMLElement>('.reading-progress > span') ?? null
        // 先读段落位置再写样式，折叠内容或吸顶高度变化后也能立即对应当前位置。
        const boundary = offset() + 2
        const index = self.progress >= .999 ? next.length - 1 : Math.max(0, next.findLastIndex(el => el.getBoundingClientRect().top <= boundary))
        if (line) gsap.set(line, { scaleX: self.progress })
        if (index !== currentIndex) {
          currentIndex = index; active.value = sections.value[index]?.id ?? ''
          next.forEach((el, i) => el.classList.toggle('is-reading', i === index))
        }
      }
      ScrollTrigger.create({ start: 0, end: 'max', onUpdate: update, onRefresh: update })
      if (bar) ScrollTrigger.create({ trigger: page.value!.querySelector('.page-heading') ?? bar, start: () => `bottom top+=${(parseFloat(getComputedStyle(bar).top) || 0) - 20}`, end: 'max', onToggle: self => { stuck.value = self.isActive } })
    }, page.value)
    media = gsap.matchMedia()
    media.add('(min-width: 900px) and (prefers-reduced-motion: no-preference)', () => {
      const decor = page.value?.querySelector('[data-parallax]'), heading = page.value?.querySelector('.page-heading')
      if (decor && heading) gsap.to(decor, { y: 28, ease: 'none', scrollTrigger: { trigger: heading, start: 'top top', end: 'bottom top', scrub: .3 } })
    }, page.value)
    layoutChanged = false; ScrollTrigger.refresh(true)
  }
  function schedule() { if (!frame && !disposed) frame = requestAnimationFrame(rebuild) }
  async function connect() {
    const current = ++revision
    cancelAnimationFrame(frame); frame = 0; connected = false
    observer?.disconnect(); resize?.disconnect(); stop(); elements.forEach(el => el.classList.remove('is-reading')); elements = []
    sections.value = []; active.value = ''; stuck.value = false
    await nextTick()
    if (disposed || current !== revision || !content.value) return
    observer = new MutationObserver(records => {
      if (records.some(record => [...record.addedNodes, ...record.removedNodes].some(node => node.nodeType === 1))) schedule()
    })
    observer.observe(content.value, { childList: true, subtree: true })
    resize = new ResizeObserver(() => { layoutChanged = true; schedule() }); resize.observe(content.value)
    if (toolbar.value) resize.observe(toolbar.value)
    schedule()
  }
  function go(id: string, keyboard = false) {
    const index = sections.value.findIndex(section => section.id === id), el = elements[index]
    if (!el) return
    const top = Math.max(0, window.scrollY + el.getBoundingClientRect().top - offset())
    window.scrollTo({ top, behavior: keyboard || window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' })
    if (keyboard) { ScrollTrigger.update(); el.tabIndex = -1; el.focus({ preventScroll: true }) }
  }
  onMounted(connect)
  watch([page, content, change], connect, { flush: 'post' })
  onUnmounted(() => { disposed = true; revision++; cancelAnimationFrame(frame); observer?.disconnect(); resize?.disconnect(); stop() })
  return { sections, active, stuck, go }
}
