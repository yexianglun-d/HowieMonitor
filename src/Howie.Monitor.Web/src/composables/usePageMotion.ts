import { nextTick, onDeactivated, onMounted, onUnmounted, watch, type Ref } from 'vue'
import { gsap } from 'gsap'

export function usePageMotion(root: Ref<HTMLElement | null>, change: () => string) {
  let media: gsap.MatchMedia | undefined
  let revision = 0, keyboard = false
  const keydown = () => { keyboard = true }
  const pointerdown = () => { keyboard = false }
  function stop() { revision++; media?.revert(); media = undefined }
  async function enter() {
    stop()
    const current = revision
    await nextTick()
    const element = root.value
    if (current !== revision || !element?.isConnected || keyboard) return
    media = gsap.matchMedia()
    media.add('(prefers-reduced-motion: no-preference)', () => {
      // 页面整体轻移，实时读数不逐项重播；快速切换先还原上一段动效。
      gsap.fromTo(element, { opacity: .88, y: 4 }, {
        opacity: 1, y: 0, duration: .22, ease: 'power2.out', overwrite: 'auto', clearProps: 'opacity,transform',
      })
    }, element)
  }
  onMounted(() => {
    document.addEventListener('keydown', keydown, true)
    document.addEventListener('pointerdown', pointerdown, true)
    void enter()
  })
  onDeactivated(stop)
  watch([root, change], enter, { flush: 'post' })
  onUnmounted(() => {
    stop()
    document.removeEventListener('keydown', keydown, true)
    document.removeEventListener('pointerdown', pointerdown, true)
  })
}
