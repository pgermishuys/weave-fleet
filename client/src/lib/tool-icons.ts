import type { Component } from "vue";
import { getTool } from "@/lib/tools";

/** The icon for a tool: the registry's, or a wrench for a tool it does not know. */
export function getToolIcon(kind: string): Component {
  return getTool(kind).icon;
}

/** The tool's name as a header ("Web Fetch"); an unknown tool's own name with a capital. */
export function getToolDisplayLabel(kind: string): string {
  return getTool(kind).heading;
}
