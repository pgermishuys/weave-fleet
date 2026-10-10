/**
 * A human-readable one-line label for a tool call, from its name and input. The registry (`@/lib/tools`) holds how
 * each tool is summarised; this is the thin entry the activity stream and the phone's fold-steps call.
 */
import { getTool } from "@/lib/tools";

export function getToolLabel(toolName: string, input: Record<string, unknown> | null): string {
  return getTool(toolName).label(input);
}
