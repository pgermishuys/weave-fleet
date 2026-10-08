// The web client's reducer calls confirmSentPrompt (a Vue composable) to settle an optimistic user bubble. This app
// sends without optimistic bubbles, so there is nothing to confirm.
export function confirmSentPrompt(_sessionId: string, _options: { correlationId?: string | null; serverMessageId?: string | null }): void {}
