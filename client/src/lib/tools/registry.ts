/**
 * The tool registry: what the client knows about each tool, in one place.
 *
 * One descriptor per canonical tool name carries its category, label, icon, heading and the flags the conversation
 * checks (does it write a file, is its title Fleet's answer, does it drive the browser). Aliases and case are
 * handled once, by `getTool`: `Read`, `read` and `mcp__fleet__fleet_page_show` all resolve here.
 *
 * Categories are how the UI groups work. `permissionKind` is the server's word for the same call
 * (`Permissions.Classify`) and decides what an ask says; the two differ on purpose (glob is `search` to the UI and
 * `read` to the server; websearch is `search` and `web`).
 */
import type { Component } from "vue";
import {
  AppWindow,
  BookOpen,
  Camera,
  CircleCheck,
  Code,
  FileText,
  Folder,
  GitBranch,
  Globe,
  Layers,
  Lightbulb,
  LightbulbOff,
  ListChecks,
  ListTodo,
  MessageCircleQuestion,
  MessagesSquare,
  MonitorUp,
  PanelsTopLeft,
  Pencil,
  Search,
  Send,
  Server,
  Terminal,
  Wrench,
} from "lucide-vue-next";
import { capitalize, shortenPath, truncate } from "./text";
import { parseToolInput, stringField, type ToolInput } from "./input";
import type { PermissionKindName } from "./permission-wording";

export type ToolCategory =
  | "read"
  | "search"
  | "edit"
  | "shell"
  | "web"
  | "subagent"
  | "question"
  | "plan"
  | "skill"
  | "fleet"
  | "other";

/** A call's input as the label builders see it: the parsed common fields, and the raw record for the rest. */
export type LabelInput = ToolInput;

export interface ToolDescriptor {
  /** Canonical name: lowercase, as the server sends it. */
  name: string;
  /** Other names the same tool goes by, any case. Punctuation is ignored (`apply_patch` is `applypatch`). */
  aliases?: readonly string[];
  category: ToolCategory;
  /** What the server calls an ask for this tool. */
  permissionKind: PermissionKindName;
  /** The tool's name as a header: "Web Fetch". */
  heading: string;
  icon: Component;
  /** A one-line summary of the call, or undefined to fall back to the tool's name. */
  label?: (input: LabelInput, fallback: string) => string | undefined;
  /** The call writes a file: `create` when it can make one, `modify` when it only changes one. */
  fileWrite?: "create" | "modify";
  /** The card's title is Fleet's answer ("Messaged Update documentation"), when Fleet gave one. */
  titledFromAnswer?: boolean;
  /** The call drives the browser, so its card offers the browser controls. */
  usesBrowser?: boolean;
  /** The label is a search pattern, drawn as a pill. */
  patternLabel?: boolean;
}

// ── Label builders ────────────────────────────────────────────────────────────

const text = (input: LabelInput, key: string): string | undefined => stringField(input.raw, key);

function shellLabel(input: LabelInput, fallback: string): string {
  if (input.description) return input.description;
  if (input.command) return truncate(input.command, 60);
  return fallback;
}

function fileLabel(input: LabelInput, fallback: string): string {
  return input.filePath ? shortenPath(input.filePath) : fallback;
}

function patternLabel(input: LabelInput, fallback: string): string {
  return input.pattern ?? fallback;
}

function openLabel(input: LabelInput, fallback: string): string {
  const what = input.command ?? input.url;
  const title = text(input, "title") ?? "";
  if (what) return title ? `${title} · ${truncate(what, 60)}` : truncate(what, 60);
  return title || fallback;
}

function pageLabel(input: LabelInput, fallback: string): string {
  const title = text(input, "title") ?? "";
  const path = text(input, "path");
  if (path) return title ? `${title} · ${truncate(shortenPath(path), 60)}` : truncate(shortenPath(path), 60);
  return title || fallback;
}

function screenshotLabel(input: LabelInput): string {
  const viewport = text(input, "viewport") ?? "desktop";
  const path = text(input, "path");
  return `screenshot${path ? ` ${truncate(path, 40)}` : ""} (${viewport})`;
}

function browserReadLabel(input: LabelInput): string {
  const what = text(input, "what") ?? "page";
  const quoted = text(input, "text");
  return `${what}${quoted ? ` “${truncate(quoted, 40)}”` : ""}`;
}

function browserActLabel(input: LabelInput): string {
  const action = text(input, "action") ?? "act";
  const target = ["url", "ref", "text"].map((key) => text(input, key)).find((value) => value !== undefined);
  return target !== undefined ? `${action} ${truncate(target, 60)}` : action;
}

function subagentLabel(input: LabelInput, fallback: string): string {
  const agent = input.agent ?? "";
  const description = input.description ?? "";
  if (agent && description) return `${agent} · ${truncate(description, 60)}`;
  return description || agent || fallback;
}

function questionLabel(input: LabelInput, fallback: string): string {
  const first = input.questions[0];
  const heading = first?.question ?? first?.header;
  return heading ? truncate(heading, 60) : fallback;
}

function codeLabel(input: LabelInput, fallback: string): string {
  const firstLine = (input.code ?? "").split("\n").find((line) => line.trim());
  return firstLine ? truncate(firstLine.trim(), 60) : fallback;
}

function todoLabel(input: LabelInput, fallback: string): string {
  const todos = input.raw.todos;
  if (!Array.isArray(todos) || todos.length === 0) return fallback;
  return `${todos.length} ${todos.length === 1 ? "todo" : "todos"}`;
}

// ── The descriptors ───────────────────────────────────────────────────────────

const DESCRIPTORS: readonly ToolDescriptor[] = [
  // Reading and searching
  { name: "read", category: "read", permissionKind: "read", heading: "Read", icon: FileText, label: fileLabel },
  { name: "list", aliases: ["ls"], category: "read", permissionKind: "read", heading: "List", icon: Folder, label: fileLabel },
  { name: "lsp", category: "read", permissionKind: "read", heading: "Lsp", icon: Wrench, label: fileLabel },
  { name: "glob", category: "search", permissionKind: "read", heading: "Glob", icon: Search, label: patternLabel, patternLabel: true },
  { name: "grep", category: "search", permissionKind: "read", heading: "Grep", icon: Search, label: patternLabel, patternLabel: true },
  { name: "codesearch", aliases: ["search"], category: "search", permissionKind: "read", heading: "Codesearch", icon: Search },

  // Writing
  { name: "edit", aliases: ["multiedit", "strreplaceeditor"], category: "edit", permissionKind: "edit", heading: "Edit", icon: Pencil, label: fileLabel, fileWrite: "modify" },
  { name: "write", category: "edit", permissionKind: "edit", heading: "Write", icon: Pencil, label: fileLabel, fileWrite: "create" },
  { name: "notebookedit", category: "edit", permissionKind: "edit", heading: "Notebook Edit", icon: Pencil, label: fileLabel, fileWrite: "create" },
  { name: "patch", aliases: ["apply_patch"], category: "edit", permissionKind: "edit", heading: "Patch", icon: Pencil, label: fileLabel, fileWrite: "modify" },

  // Running
  { name: "bash", category: "shell", permissionKind: "shell", heading: "Bash", icon: Terminal, label: shellLabel },
  { name: "shell", aliases: ["terminal"], category: "shell", permissionKind: "shell", heading: "Shell", icon: Terminal, label: shellLabel },
  { name: "execute", category: "shell", permissionKind: "shell", heading: "Code", icon: Code, label: codeLabel, usesBrowser: true },

  // The web
  { name: "webfetch", aliases: ["fetch"], category: "web", permissionKind: "web", heading: "Web Fetch", icon: Globe, label: (input, fallback) => input.url ?? fallback },
  { name: "websearch", category: "search", permissionKind: "web", heading: "Web Search", icon: Search, label: (input, fallback) => (input.query ? truncate(input.query, 60) : fallback) },

  // The agent's own tools
  { name: "task", aliases: ["agent"], category: "subagent", permissionKind: "read", heading: "Task", icon: GitBranch },
  { name: "subagent", category: "subagent", permissionKind: "read", heading: "Subagent", icon: GitBranch, label: subagentLabel },
  { name: "skill", category: "skill", permissionKind: "read", heading: "Skill", icon: Layers, label: (input, fallback) => input.skill ?? fallback },
  { name: "question", category: "question", permissionKind: "read", heading: "Question", icon: MessageCircleQuestion, label: questionLabel },
  { name: "todowrite", aliases: ["todo"], category: "plan", permissionKind: "read", heading: "Update todos", icon: ListTodo, label: todoLabel },
  { name: "todoread", category: "plan", permissionKind: "read", heading: "Read todos", icon: ListChecks },

  // Fleet's own tools
  { name: "fleet_app_start", category: "fleet", permissionKind: "read", heading: "Run app", icon: AppWindow, label: openLabel, titledFromAnswer: true },
  { name: "fleet_browser_open", category: "fleet", permissionKind: "read", heading: "Open page", icon: Globe, label: openLabel, titledFromAnswer: true },
  { name: "fleet_browser_screenshot", category: "fleet", permissionKind: "read", heading: "Screenshot", icon: Camera, label: screenshotLabel, titledFromAnswer: true },
  { name: "fleet_browser_read", category: "fleet", permissionKind: "read", heading: "Read page", icon: Globe, label: browserReadLabel, usesBrowser: true },
  { name: "fleet_browser_act", category: "fleet", permissionKind: "read", heading: "Use page", icon: Globe, label: browserActLabel, usesBrowser: true },
  { name: "fleet_page_show", category: "fleet", permissionKind: "read", heading: "Show page", icon: PanelsTopLeft, label: pageLabel, titledFromAnswer: true },
  { name: "fleet_walkthrough_show", category: "fleet", permissionKind: "read", heading: "Walkthrough", icon: BookOpen, label: (input, fallback) => text(input, "title") ?? fallback, titledFromAnswer: true },
  { name: "fleet_message", category: "fleet", permissionKind: "read", heading: "Message session", icon: Send, titledFromAnswer: true },
  { name: "fleet_session_read", category: "fleet", permissionKind: "read", heading: "Read session", icon: MessagesSquare, titledFromAnswer: true },
  { name: "fleet_machine_list", category: "fleet", permissionKind: "read", heading: "List machines", icon: Server, titledFromAnswer: true },
  { name: "fleet_session_start", category: "fleet", permissionKind: "read", heading: "Start session on a machine", icon: MonitorUp, titledFromAnswer: true },
  { name: "fleet_memory_save", category: "fleet", permissionKind: "read", heading: "Remember", icon: Lightbulb, label: (input) => { const note = text(input, "text"); return note ? truncate(note, 80) : "remember"; } },
  { name: "fleet_memory_forget", category: "fleet", permissionKind: "read", heading: "Forget note", icon: LightbulbOff, label: (input) => text(input, "id") ?? "forget note" },
  { name: "fleet_mod_write", category: "fleet", permissionKind: "read", heading: "Write mod", icon: Pencil, label: (input) => text(input, "name") ?? "write mod" },
  { name: "fleet_mod_check", category: "fleet", permissionKind: "read", heading: "Check mod", icon: CircleCheck, label: (input) => text(input, "name") ?? "check mod" },
  { name: "fleet_mod_reload", category: "fleet", permissionKind: "read", heading: "Reload mod", icon: Code, label: (input) => text(input, "name") ?? "reload mod" },
  { name: "fleet_mod_test", category: "fleet", permissionKind: "read", heading: "Test mod", icon: Code, label: (input) => text(input, "name") ?? "test mod" },
  { name: "fleet_mod_keep", category: "fleet", permissionKind: "read", heading: "Keep mod", icon: CircleCheck, label: (input) => text(input, "name") ?? "keep mod" },
  { name: "fleet_mod_list", category: "fleet", permissionKind: "read", heading: "List mods", icon: ListChecks, label: () => "list mods" },
  { name: "fleet_step_done", category: "fleet", permissionKind: "read", heading: "Step done", icon: CircleCheck },
];

/**
 * Tools the server classifies for permission asks that the UI has nothing else to say about, lowercased.
 * (`Permissions.Classify`: ReadTools, EditTools and ShellTools.)
 */
const SERVER_ONLY_KINDS: Readonly<Record<string, PermissionKindName>> = {
  notebookread: "read",
  exitplanmode: "read",
  listmcpresourcestool: "read",
  readmcpresourcetool: "read",
  opencode_list_mcp_resources: "read",
  opencode_read_mcp_resource: "read",
  move: "edit",
  bashoutput: "shell",
  killshell: "shell",
  killbash: "shell",
};

// ── Lookup ────────────────────────────────────────────────────────────────────

/** `Apply_Patch` → `applypatch`: case and punctuation do not tell tools apart. */
function compact(name: string): string {
  return name.toLowerCase().replace(/[^a-z0-9]/g, "");
}

const BY_KEY = new Map<string, ToolDescriptor>();
for (const descriptor of DESCRIPTORS) {
  for (const name of [descriptor.name, ...(descriptor.aliases ?? [])]) {
    const key = compact(name);
    if (BY_KEY.has(key)) throw new Error(`Tool name ${name} is claimed twice.`);
    BY_KEY.set(key, descriptor);
  }
}

/** `mcp__fleet__fleet_page_show` → its server and tool; any other name → null. */
export function parseMcpName(name: string): { server: string; tool: string } | null {
  if (!name.startsWith("mcp__")) return null;
  const rest = name.slice("mcp__".length);
  const split = rest.indexOf("__");
  if (split <= 0 || split + 2 >= rest.length) return null;
  return { server: rest.slice(0, split), tool: rest.slice(split + 2) };
}

/** A tool, resolved: everything the client knows about a name, with fallbacks for names it does not know. */
export interface ResolvedTool {
  /** The name as it arrived. */
  raw: string;
  /** The canonical name when the tool is known; otherwise the lowercased name without any `mcp__server__` prefix. */
  name: string;
  /** A descriptor exists for it. */
  known: boolean;
  descriptor: ToolDescriptor | undefined;
  mcp: { server: string; tool: string } | null;
  category: ToolCategory;
  permissionKind: PermissionKindName;
  icon: Component;
  /** The name as a header: "Web Fetch"; for an unknown tool its name with a capital. */
  heading: string;
  /** A short name for lists: the tool part of an MCP name, capitalised ("Lookup_order"). */
  displayName: string;
  fileWrite: "create" | "modify" | undefined;
  titledFromAnswer: boolean;
  usesBrowser: boolean;
  patternLabel: boolean;
  /** A one-line summary of the call; the tool's name (as it arrived) when the input has nothing to say. */
  label(input: unknown): string;
}

const cache = new Map<string, ResolvedTool>();

function resolve(raw: string): ResolvedTool {
  const mcp = parseMcpName(raw);
  // Fleet's own MCP server is Fleet's tools under another name; any other server's tools are nobody's we know.
  const lookup = mcp?.server === "fleet" ? mcp.tool : mcp ? undefined : raw;
  const descriptor = lookup === undefined ? undefined : BY_KEY.get(compact(lookup));
  const lowered = (mcp ? mcp.tool : raw).toLowerCase();

  let permissionKind: PermissionKindName = "other";
  let category: ToolCategory = "other";
  if (descriptor) {
    permissionKind = descriptor.permissionKind;
    category = descriptor.category;
  } else if (!mcp) {
    if (lowered.startsWith("fleet_")) {
      permissionKind = "read";
      category = "fleet";
    } else {
      permissionKind = SERVER_ONLY_KINDS[lowered] ?? "other";
      category = permissionKind === "other" ? "other" : permissionKind;
    }
  }

  const label = (input: unknown): string => {
    const parsed = parseToolInput(input);
    return descriptor?.label?.(parsed, raw) ?? raw;
  };

  return {
    raw,
    name: descriptor?.name ?? lowered,
    known: descriptor !== undefined,
    descriptor,
    mcp,
    category,
    permissionKind,
    icon: descriptor?.icon ?? Wrench,
    heading: descriptor?.heading ?? capitalize(raw),
    displayName: capitalize(mcp ? mcp.tool : raw),
    fileWrite: descriptor?.fileWrite,
    titledFromAnswer: descriptor?.titledFromAnswer === true,
    usesBrowser: descriptor?.usesBrowser === true,
    patternLabel: descriptor?.patternLabel === true,
    label,
  };
}

/** Everything the client knows about a tool name, in any case and under any alias. Never throws and never returns nothing. */
export function getTool(name: string | null | undefined): ResolvedTool {
  const raw = name ?? "";
  let tool = cache.get(raw);
  if (!tool) {
    tool = resolve(raw);
    cache.set(raw, tool);
  }
  return tool;
}

/** The descriptor for a name, or undefined when the client has none. */
export function findTool(name: string | null | undefined): ToolDescriptor | undefined {
  return getTool(name).descriptor;
}

/** Every descriptor, for tests and for screens that list the tools. */
export function allTools(): readonly ToolDescriptor[] {
  return DESCRIPTORS;
}
