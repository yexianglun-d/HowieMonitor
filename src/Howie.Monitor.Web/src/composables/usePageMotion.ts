import { nextTick, onMounted, onUnmounted, watch, type Ref } from 'vue'
import { gsap } from 'gsap'

export function usePageMotion(root: Ref<HTMLElement | null>, change: () => string) {
  let media: gsap.MatchMedia | undefined
  let revision = 0
  async function enter() {
    const current = ++revision
    await nextTick()
    if (current !== revision || !root.value) return
    media?.revert()
    media = gsap.matchMedia()
    media.add('(prefers-reduced-motion: no-preference)', () => {
      const element = root.value!
      const targets = [...(element.matches('[data-enter]') ? [element] : []), ...element.querySelectorAll('[data-enter]')]
      gsap.fromTo(targets, { opacity: 0, y: 12 }, {
        opacity: 1, y: 0, duration: .34, stagger: { amount: .12 }, ease: 'power2.out', overwrite: 'auto', clearProps: 'opacity,transform',
      })
    }, root.value)
  }
  onMounted(enter)
  watch(change, enter, { flush: 'post' })
  onUnmounted(() => { revision++; media?.revert() })
}
