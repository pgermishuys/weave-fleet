import { readonly, ref, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue"

export interface QueuedMessage {
  id: string
  text: string
  agent?: string
  model?: { providerID: string; modelID: string }
}

export interface UseMessageQueueResult {
  queue: Readonly<Ref<readonly QueuedMessage[]>>
  enqueue: (text: string, agent?: string, model?: { providerID: string; modelID: string }) => void
  removeAt: (index: number) => void
  clear: () => void
  isAutoSending: Readonly<ShallowRef<boolean>>
}

let nextQueueId = 0

export function useMessageQueue(
  sessionStatus: MaybeRefOrGetter<"idle" | "busy">,
  onSend?: (text: string, agent?: string, model?: { providerID: string; modelID: string }) => Promise<void>,
): UseMessageQueueResult {
  const queue = ref<QueuedMessage[]>([])
  const isAutoSending = shallowRef(false)
  const previousStatus = shallowRef<"idle" | "busy">(toValue(sessionStatus))

  function enqueue(text: string, agent?: string, model?: { providerID: string; modelID: string }): void {
    queue.value = [...queue.value, { id: `queue-${++nextQueueId}`, text, agent, model }]
  }

  function removeAt(index: number): void {
    queue.value = queue.value.filter((_, currentIndex) => currentIndex !== index)
  }

  function clear(): void {
    queue.value = []
  }

  watch(
    () => [toValue(sessionStatus), queue.value.length] as const,
    ([nextStatus]) => {
      const wasBusy = previousStatus.value === "busy"
      previousStatus.value = nextStatus

      if (!wasBusy || nextStatus !== "idle" || queue.value.length === 0 || !onSend) {
        return
      }

      const [nextMessage, ...remaining] = queue.value
      if (!nextMessage) {
        return
      }

      queue.value = remaining
      isAutoSending.value = true

      void onSend(nextMessage.text, nextMessage.agent, nextMessage.model).finally(() => {
        isAutoSending.value = false
      })
    },
  )

  return {
    queue: readonly(queue),
    enqueue,
    removeAt,
    clear,
    isAutoSending: readonly(isAutoSending),
  }
}
