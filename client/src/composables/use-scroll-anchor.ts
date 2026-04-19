import { onUnmounted, readonly, shallowRef, toValue, watch, type MaybeRefOrGetter, type ShallowRef } from "vue"

const AT_BOTTOM_THRESHOLD = 50
const NEAR_TOP_THRESHOLD = 200

export interface UseScrollAnchorOptions {
  messageCount: MaybeRefOrGetter<number>
  externalSuppressAutoScroll?: ShallowRef<boolean>
}

export interface UseScrollAnchorReturn {
  scrollRef: (node: HTMLElement | null) => void
  isAtBottom: Readonly<ShallowRef<boolean>>
  isNearTop: Readonly<ShallowRef<boolean>>
  newMessageCount: Readonly<ShallowRef<number>>
  scrollToBottom: () => void
  preserveScrollPosition: (callback: () => void | Promise<void>) => Promise<void>
  getScrollPosition: () => { scrollTop: number; scrollHeight: number } | null
  restoreScrollPosition: (saved: { scrollTop: number; scrollHeight: number }) => void
  suppressAutoScroll: ShallowRef<boolean>
  viewportRef: ShallowRef<HTMLElement | null>
  viewportElement: ShallowRef<HTMLElement | null>
}

export function useScrollAnchor(options: UseScrollAnchorOptions): UseScrollAnchorReturn {
  const viewportRef = shallowRef<HTMLElement | null>(null)
  const viewportElement = shallowRef<HTMLElement | null>(null)
  const isAtBottom = shallowRef(true)
  const isNearTop = shallowRef(false)
  const newMessageCount = shallowRef(0)
  const previousMessageCount = shallowRef(toValue(options.messageCount))
  const internalSuppressAutoScroll = shallowRef(false)
  const suppressAutoScroll = options.externalSuppressAutoScroll ?? internalSuppressAutoScroll

  let scrollListenerTarget: HTMLElement | null = null
  let rafId: number | null = null
  let mutationRafId: number | null = null
  let mutationObserver: MutationObserver | null = null
  let lastScrollHeight = 0
  let isProgrammaticScroll = false

  function clearProgrammaticScroll(afterMs: number): void {
    window.setTimeout(() => {
      isProgrammaticScroll = false
    }, afterMs)
  }

  function checkIsAtBottom(element: HTMLElement): boolean {
    return element.scrollHeight - element.scrollTop - element.clientHeight <= AT_BOTTOM_THRESHOLD
  }

  function scrollViewportToBottom(behavior: ScrollBehavior): void {
    const element = viewportRef.value
    if (!element) {
      return
    }

    isProgrammaticScroll = true
    element.scrollTo({ top: element.scrollHeight, behavior })
    lastScrollHeight = element.scrollHeight
    isAtBottom.value = true
    newMessageCount.value = 0
    clearProgrammaticScroll(behavior === "smooth" ? 300 : 0)
  }

  function handleScroll(): void {
    if (rafId !== null) {
      return
    }

    rafId = window.requestAnimationFrame(() => {
      rafId = null

      const element = viewportRef.value
      if (!element) {
        return
      }

      const atBottom = checkIsAtBottom(element)
      if (isProgrammaticScroll) {
        if (atBottom) {
          isAtBottom.value = true
          newMessageCount.value = 0
        }
        return
      }

      isAtBottom.value = atBottom
      isNearTop.value = element.scrollTop <= NEAR_TOP_THRESHOLD

      if (atBottom) {
        newMessageCount.value = 0
      }
    })
  }

  function cleanupViewport(): void {
    if (scrollListenerTarget) {
      scrollListenerTarget.removeEventListener("scroll", handleScroll)
      scrollListenerTarget = null
    }

    if (mutationObserver) {
      mutationObserver.disconnect()
      mutationObserver = null
    }
  }

  function scrollRef(node: HTMLElement | null): void {
    cleanupViewport()

    if (!node) {
      viewportRef.value = null
      viewportElement.value = null
      return
    }

    const viewport = node.querySelector<HTMLElement>('[data-slot="scroll-area-viewport"]')
    viewportRef.value = viewport ?? null
    viewportElement.value = viewport ?? null
    scrollListenerTarget = viewport ?? null

    if (!viewport) {
      return
    }

    viewport.addEventListener("scroll", handleScroll, { passive: true })
    lastScrollHeight = viewport.scrollHeight

    mutationObserver = new MutationObserver(() => {
      if (!isAtBottom.value || isProgrammaticScroll || mutationRafId !== null) {
        return
      }

      mutationRafId = window.requestAnimationFrame(() => {
        mutationRafId = null

        const element = viewportRef.value
        if (!element || !isAtBottom.value || element.scrollHeight <= lastScrollHeight) {
          return
        }

        lastScrollHeight = element.scrollHeight
        isProgrammaticScroll = true
        element.scrollTop = element.scrollHeight
        isProgrammaticScroll = false
      })
    })

    mutationObserver.observe(viewport, {
      childList: true,
      subtree: true,
      characterData: true,
    })

    if (toValue(options.messageCount) > 0 && !suppressAutoScroll.value) {
      window.requestAnimationFrame(() => {
        if (viewportRef.value !== viewport || suppressAutoScroll.value) {
          return
        }

        scrollViewportToBottom("auto")
      })
    }
  }

  function scrollToBottom(): void {
    scrollViewportToBottom("smooth")
  }

  async function preserveScrollPosition(callback: () => void | Promise<void>): Promise<void> {
    const element = viewportRef.value
    if (!element) {
      await callback()
      return
    }

    const previousScrollHeight = element.scrollHeight
    const previousScrollTop = element.scrollTop

    isProgrammaticScroll = true
    await callback()

    window.requestAnimationFrame(() => {
      const delta = element.scrollHeight - previousScrollHeight
      if (delta > 0) {
        element.scrollTop = previousScrollTop + delta
      }

      isProgrammaticScroll = false
    })
  }

  function getScrollPosition(): { scrollTop: number; scrollHeight: number } | null {
    const element = viewportRef.value
    if (!element) {
      return null
    }

    return {
      scrollTop: element.scrollTop,
      scrollHeight: element.scrollHeight,
    }
  }

  function restoreScrollPosition(saved: { scrollTop: number; scrollHeight: number }): void {
    const element = viewportRef.value
    if (!element) {
      return
    }

    isProgrammaticScroll = true
    element.scrollTop = saved.scrollTop + (element.scrollHeight - saved.scrollHeight)

    const atBottom = checkIsAtBottom(element)
    isAtBottom.value = atBottom
    if (atBottom) {
      newMessageCount.value = 0
    }

    clearProgrammaticScroll(300)
  }

  watch(
    () => toValue(options.messageCount),
    (messageCount) => {
      const delta = messageCount - previousMessageCount.value
      previousMessageCount.value = messageCount

      if (delta <= 0 || suppressAutoScroll.value) {
        return
      }

      if (isAtBottom.value) {
        window.requestAnimationFrame(() => {
          scrollViewportToBottom("smooth")
        })
        return
      }

      newMessageCount.value += delta
    },
  )

  onUnmounted(() => {
    if (rafId !== null) {
      window.cancelAnimationFrame(rafId)
    }

    if (mutationRafId !== null) {
      window.cancelAnimationFrame(mutationRafId)
    }

    cleanupViewport()
  })

  return {
    scrollRef,
    isAtBottom: readonly(isAtBottom),
    isNearTop: readonly(isNearTop),
    newMessageCount: readonly(newMessageCount),
    scrollToBottom,
    preserveScrollPosition,
    getScrollPosition,
    restoreScrollPosition,
    suppressAutoScroll,
    viewportRef,
    viewportElement,
  }
}
