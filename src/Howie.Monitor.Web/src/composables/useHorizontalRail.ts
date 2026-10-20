import { nextTick, onMounted, onUnmounted, ref, watch, type Ref } from 'vue'

export function useHorizontalRail(track: Ref<HTMLElement | null>, change: () => string) {
  const before = ref(false), after = ref(false), progress = ref(0)
  let resize: ResizeObserver | undefined, observed: HTMLElement | null = null, revision = 0, disposed = false
  function measure() {
    const el = track.value
    if (!el) { before.value = after.value = false; progress.value = 0; return }
    const maximum = el.scrollWidth - el.clientWidth
    before.value = el.scrollLeft > 2; after.value = maximum - el.scrollLeft > 2
    progress.value = maximum > 2 ? Math.min(1, Math.max(0, el.scrollLeft / maximum)) : 1
  }
  function move(direction: number, keyboard = false) {
    const el = track.value
    if (!el) return
    const step = (el.firstElementChild as HTMLElement | null)?.offsetWidth ?? el.clientWidth * .8
    el.scrollBy({ left: direction * (step + (parseFloat(getComputedStyle(el).columnGap) || 0)), behavior: keyboard || window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' })
  }
  function keydown(event: KeyboardEvent) {
    if (event.target !== event.currentTarget || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return
    event.preventDefault(); move(event.key === 'ArrowRight' ? 1 : -1, true)
  }
  async function connect() {
    const current = ++revision
    await nextTick()
    if (disposed || current !== revision) return
    resize?.disconnect()
    observed?.removeEventListener('scroll', measure)
    observed = track.value
    observed?.addEventListener('scroll', measure, { passive: true })
    resize = new ResizeObserver(measure)
    if (observed) { resize.observe(observed); for (const child of observed.children) resize.observe(child) }
    measure()
  }
  onMounted(connect)
  watch([track, change], connect, { flush: 'post' })
  onUnmounted(() => { disposed = true; revision++; resize?.disconnect(); observed?.removeEventListener('scroll', measure) })
  return { before, after, progress, move, keydown }
}
