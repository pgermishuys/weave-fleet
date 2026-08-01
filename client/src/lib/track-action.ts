import { api } from '@/api/client'

export function trackAction(action: string, sessionId?: string, metadata?: Record<string, unknown>): void {
  api.POST('/api/telemetry/actions', {
    body: { action, sessionId, metadata } as never,
  }).catch(() => {})
}
