import { ref, shallowRef, toValue, type MaybeRefOrGetter } from "vue"
import { apiFetch } from "@/lib/api-client"

/**
 * Sends a message to a session's agent when the user asks (a failing check, a review comment), keyed so each
 * button shows its own "Sending…" and "Sent".
 */
export function useSendToAgent(sessionId: MaybeRefOrGetter<string>) {
  const sending = shallowRef<ReadonlySet<string>>(new Set())
  const sent = shallowRef<ReadonlySet<string>>(new Set())
  const error = ref<string | null>(null)

  async function send(key: string, text: string): Promise<boolean> {
    if (sending.value.has(key)) return false
    sending.value = new Set([...sending.value, key])
    error.value = null
    try {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(toValue(sessionId))}/prompt`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ text, userMessageId: crypto.randomUUID() }),
      })
      if (response.ok) {
        sent.value = new Set([...sent.value, key])
        return true
      }
      error.value = `Couldn't send to the agent (HTTP ${response.status}).`
    } catch {
      error.value = "Couldn't reach Fleet to send this to the agent."
    } finally {
      const next = new Set(sending.value)
      next.delete(key)
      sending.value = next
    }
    return false
  }

  return { send, sending, sent, error }
}
