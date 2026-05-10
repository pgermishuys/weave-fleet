import { apiFetch } from './api-client'

export function trackAction(action: string, sessionId?: string, metadata?: Record<string, unknown>): void {
  apiFetch('/api/telemetry/actions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, sessionId, metadata }),
  }).catch(() => {})
}
