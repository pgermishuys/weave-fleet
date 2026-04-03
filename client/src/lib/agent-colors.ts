/**
 * Resolve an agent's display color.
 *
 * Uses the API-provided color if available, otherwise deterministically
 * assigns a color from a palette based on the agent name.
 * No agent names are hardcoded — the fleet UI is agent-name agnostic.
 */

const PALETTE = [
  "#4A90D9", "#D94A4A", "#E67E22", "#9B59B6",
  "#27AE60", "#F39C12", "#1ABC9C", "#E74C3C",
  "#3498DB", "#2ECC71", "#E06C75", "#61AFEF",
  "#C678DD", "#98C379", "#D19A66", "#56B6C2",
];

function hashString(s: string): number {
  let hash = 0;
  for (let i = 0; i < s.length; i++) {
    hash = ((hash << 5) - hash + s.charCodeAt(i)) | 0;
  }
  return Math.abs(hash);
}

export function resolveAgentColor(agentName: string, apiColor?: string): string {
  if (apiColor) return apiColor;
  return PALETTE[hashString(agentName.toLowerCase()) % PALETTE.length];
}
