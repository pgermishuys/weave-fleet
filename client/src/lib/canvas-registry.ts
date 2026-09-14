import { defineAsyncComponent, type Component } from "vue";
import {
  File,
  FileCode,
  FileJson,
  FileText,
  FolderTree,
  GitCompare,
  Globe,
  ListChecks,
  Paperclip,
  Workflow,
} from "lucide-vue-next";
import BrowserCanvas from "@/components/canvas/BrowserCanvas.vue";
import ChangesCanvas from "@/components/canvas/ChangesCanvas.vue";
import FilesCanvas from "@/components/canvas/FilesCanvas.vue";
import ProgressCanvas from "@/components/canvas/ProgressCanvas.vue";
import VisualCanvas from "@/components/canvas/VisualCanvas.vue";
import SessionContextCanvas from "@/components/session-context/SessionContextCanvas.vue";
import type { CanvasInstance, CanvasKind } from "@/stores/canvases";
import { visualCanvasTitle } from "@/stores/canvases";
import type { VisualPayload } from "@/lib/visual-payload";

/** A count or an attention dot shown on a canvas tab. */
export interface CanvasTabBadge {
  /** A number, or short text such as "7/17". */
  count?: number | string;
  attention?: boolean;
  /** Screen-reader text for the badge. */
  label?: string;
}

export interface CanvasTypeDefinition {
  kind: CanvasKind;
  label: string;
  icon: Component;
  component: Component;
}

// The editor is its own chunk (CodeMirror and its languages), loaded when the first file opens.
const FileCanvas = defineAsyncComponent(() => import("@/components/canvas/FileCanvas.vue").then((module) => module.default));

export const CANVAS_TYPES: Record<CanvasKind, CanvasTypeDefinition> = {
  changes: { kind: "changes", label: "Changes", icon: GitCompare, component: ChangesCanvas },
  files: { kind: "files", label: "Files", icon: FolderTree, component: FilesCanvas },
  context: { kind: "context", label: "Context", icon: Paperclip, component: SessionContextCanvas },
  progress: { kind: "progress", label: "Progress", icon: ListChecks, component: ProgressCanvas },
  visual: { kind: "visual", label: "Diagram", icon: Workflow, component: VisualCanvas },
  browser: { kind: "browser", label: "Browser", icon: Globe, component: BrowserCanvas },
  file: { kind: "file", label: "File", icon: File, component: FileCanvas },
};

/** Built-in canvases a person can open from the + menu. */
export const PICKABLE_CANVAS_KINDS = ["context", "progress", "changes", "files"] as const;

const VISUAL_ICONS: Record<VisualPayload["$type"], Component> = {
  "visual/flow": Workflow,
  "visual/sequence": Workflow,
  markdown: FileText,
  html: Globe,
};

export function visualIcon(payload: VisualPayload): Component {
  return VISUAL_ICONS[payload.$type];
}

const CODE_FILE = /\.(?:[cm]?[jt]sx?|vue|svelte|cs|fs|go|rs|py|rb|java|kt|swift|c|cc|cpp|h|hpp|php|sh|ps1|sql|css|scss|less|html?|xml|csproj|props|targets|slnx|ya?ml|toml)$/i;

export function fileName(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1) || path;
}

export function fileIcon(path: string): Component {
  if (/\.(?:json|jsonc|json5)$/i.test(path)) return FileJson;
  if (/\.(?:md|markdown|mdx|txt|rst)$/i.test(path)) return FileText;
  if (CODE_FILE.test(path)) return FileCode;
  return File;
}

export function canvasTitle(canvas: CanvasInstance): string {
  if (canvas.file) return fileName(canvas.file.path);
  if (canvas.browser) return canvas.browser.title;
  return canvas.payload ? visualCanvasTitle(canvas.payload) : CANVAS_TYPES[canvas.kind].label;
}

export function canvasIcon(canvas: CanvasInstance): Component {
  if (canvas.file) return fileIcon(canvas.file.path);
  return canvas.payload ? visualIcon(canvas.payload) : CANVAS_TYPES[canvas.kind].icon;
}
