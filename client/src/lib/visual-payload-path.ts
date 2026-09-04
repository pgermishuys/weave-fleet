import type { VisualPayload } from '@/lib/visual-payload'

export const VISUAL_SYNTHETIC_PATH_PREFIX = '__visual__/'

/**
 * Derives the synthetic content-panel path used to route a visual payload
 * through the `[content]` slot. Shared between `SessionMetadataHeader.vue`
 * and `SessionsV2RightPanel.vue` so both produce identical paths for the
 * same payload (keeping selection highlighting and routing in sync).
 */
export function getVisualPayloadSyntheticPath(payload: VisualPayload): string {
  const name = payload.sourceFilePath ?? payload.title ?? 'artifact'
  return `${VISUAL_SYNTHETIC_PATH_PREFIX}${name}`
}
