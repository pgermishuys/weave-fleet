/**
 * Characterization: how the client classifies tools today.
 *
 * Tool knowledge lives in a dozen places (labels, icons, turns, the phone's fold-steps, the tool card, pr-utils, the
 * question checks, the lineage panel). This pins what each one says about a common table of names, so a later change
 * that moves them onto one registry shows exactly what moved. Where the sites disagree, the row says so as it is: the
 * DRIFT notes below name each disagreement. Nothing here is a statement of what is right.
 */
import { describe, expect, it } from "vitest";
import * as lucide from "lucide-vue-next";
import type { AccumulatedMessage, AccumulatedPart, AccumulatedToolPart } from "@/lib/client-types";
import { clearTurnDerivationCache, deriveTurns } from "@/lib/turns";
import { getToolIcon, getToolDisplayLabel } from "@/lib/tool-icons";
import { getToolLabel } from "@/lib/tool-labels";
import { isBashTool } from "@/lib/pr-utils";
import { isQuestionPart } from "@/lib/question-types";
import { summarizeAgentActivity } from "@/lib/session-lineage";
import { pendingQuestion } from "@/lib/phone/dock-state";
import { foldMessages, stepCategory, stepRow, type FoldedStep } from "@/lib/phone/fold-steps";
import { isSubagentTool, toToolCardItem } from "@/components/session/activity-stream-tool-card";

const NAMES = [
  // OpenCode's own names
  "read", "write", "edit", "patch", "apply_patch", "list", "glob", "grep", "bash", "webfetch", "websearch", "task", "skill",
  "question", "todowrite", "todoread", "codesearch", "lsp",
  // Claude Code's names, as the server lowercases them, and as they might still arrive
  "multiedit", "notebookedit", "strreplaceeditor", "terminal", "Read", "Edit", "MultiEdit", "NotebookEdit", "Bash", "Task",
  "TodoWrite", "WebFetch", "Agent", "LS",
  // OpenCode 2
  "shell", "execute", "subagent", "Subagent", "SHELL",
  // Fleet's tools, and MCP tools
  "fleet_app_start", "fleet_browser_open", "fleet_browser_screenshot", "fleet_browser_read", "fleet_browser_act",
  "fleet_page_show", "fleet_walkthrough_show", "fleet_message", "fleet_session_read", "fleet_machine_list",
  "fleet_session_start", "fleet_memory_save", "fleet_memory_forget", "fleet_step_done", "fleet_canvas_open",
  "mcp__fleet__fleet_page_show", "mcp__acme__lookup_order",
  // Nobody knows these
  "frobnicate", "",
] as const;

const RICH_INPUT = {
  filePath: "src/widgets/panel.ts",
  command: "dotnet test",
  pattern: "*.ts",
  url: "https://example.test/docs",
  query: "reconnect backoff",
  name: "fleet-run",
  agent: "explore",
  description: "Check the panel",
  code: "await page.open()\nsecond line",
  questions: [{ question: "Which folder?", header: "Folder" }],
  title: "Panel",
  content: "one\ntwo",
};

// lucide exports every icon under three names (Pencil, PencilIcon, LucidePencil); the plain one is the one we use.
const ICONS = new Map<unknown, string>(
  Object.entries(lucide)
    .filter(([key]) => !key.startsWith("Lucide") && !key.endsWith("Icon"))
    .sort(([a], [b]) => (a < b ? 1 : -1))
    .map(([key, value]) => [value, key] as const),
);

function iconName(tool: string): string {
  return ICONS.get(getToolIcon(tool)) ?? "?";
}

function part(tool: string, state: Record<string, unknown>): AccumulatedToolPart {
  return { partId: `p-${tool}`, type: "tool", tool, callId: `c-${tool}`, state } as AccumulatedToolPart;
}

function assistant(parts: AccumulatedPart[]): AccumulatedMessage {
  return { messageId: "a1", sessionId: "s", role: "assistant", parts, createdAt: 2 };
}

function user(): AccumulatedMessage {
  return { messageId: "u1", sessionId: "s", role: "user", parts: [{ partId: "u1t", type: "text", text: "go" }], createdAt: 1 };
}

interface Row {
  label: [string, string];
  icon: string;
  heading: string;
  turns: { shell: boolean; file: boolean; created: boolean };
  fold: { category: string; subagent: boolean; pattern: boolean };
  card: { subagent: boolean; pattern: boolean; titledFromAnswer: boolean };
  prShell: boolean;
  question: { part: boolean; pending: boolean };
  lineage: string;
}

function rowFor(tool: string): Row {
  clearTurnDerivationCache();
  const rich = part(tool, { status: "completed", input: RICH_INPUT });
  const turn = deriveTurns([user(), assistant([rich])])[0];

  const folded = foldMessages([assistant([rich])]);
  const step: FoldedStep | undefined = folded.flatMap((block) => (block.kind === "steps" ? block.steps : []))[0];

  const titled = toToolCardItem(part(tool, { status: "completed", input: {}, title: "FLEET SAID" }));
  const plain = toToolCardItem(rich);

  const running = part(tool, { status: "running", input: { questions: [{ question: "Which folder?", options: [] }] } });
  const lineage = summarizeAgentActivity([assistant([rich])]);

  return {
    label: [getToolLabel(tool, null), getToolLabel(tool, RICH_INPUT)],
    icon: iconName(tool),
    heading: getToolDisplayLabel(tool),
    turns: { shell: (turn?.commands.length ?? 0) > 0, file: (turn?.files.length ?? 0) > 0, created: turn?.files[0]?.created ?? false },
    fold: {
      category: stepCategory(tool),
      subagent: folded.some((block) => block.kind === "subagent"),
      pattern: step ? stepRow(step).pattern : false,
    },
    card: { subagent: isSubagentTool(tool), pattern: plain.isPatternTool === true, titledFromAnswer: titled.title === "FLEET SAID" },
    prShell: isBashTool(tool),
    question: { part: isQuestionPart(rich), pending: pendingQuestion([assistant([running])]) !== null },
    lineage: lineage.topTools[0] ?? "",
  };
}

/**
 * What every site says today, one row per name. Read the DRIFT notes under the table for where the rows disagree.
 * Icons are lucide names (an alias such as CheckCircle2 for CircleCheck is the same component).
 */
const TODAY: Record<string, Row> = {
  "read": {label: ["read", "src/widgets/panel.ts"], icon: "FileText", heading: "Read", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Read"},
  "write": {label: ["write", "src/widgets/panel.ts"], icon: "Pencil", heading: "Write", turns: {shell: false, file: true, created: true}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Write"},
  "edit": {label: ["edit", "src/widgets/panel.ts"], icon: "Pencil", heading: "Edit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Edit"},
  "patch": {label: ["patch", "patch"], icon: "Wrench", heading: "Patch", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Patch"},
  "apply_patch": {label: ["apply_patch", "apply_patch"], icon: "Wrench", heading: "Apply_patch", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Apply_patch"},
  "list": {label: ["list", "list"], icon: "Wrench", heading: "List", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "List"},
  "glob": {label: ["glob", "*.ts"], icon: "Search", heading: "Glob", turns: {shell: false, file: false, created: false}, fold: {category: "search", subagent: false, pattern: true}, card: {subagent: false, pattern: true, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Glob"},
  "grep": {label: ["grep", "*.ts"], icon: "Search", heading: "Grep", turns: {shell: false, file: false, created: false}, fold: {category: "search", subagent: false, pattern: true}, card: {subagent: false, pattern: true, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Grep"},
  "bash": {label: ["bash", "Check the panel"], icon: "Terminal", heading: "Bash", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "Bash"},
  "webfetch": {label: ["webfetch", "https://example.test/docs"], icon: "Globe", heading: "Web Fetch", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Webfetch"},
  "websearch": {label: ["websearch", "reconnect backoff"], icon: "Search", heading: "Web Search", turns: {shell: false, file: false, created: false}, fold: {category: "search", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Websearch"},
  "task": {label: ["task", "task"], icon: "GitBranch", heading: "Task", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Task"},
  "skill": {label: ["skill", "fleet-run"], icon: "Layers", heading: "Skill", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Skill"},
  "question": {label: ["question", "Which folder?"], icon: "MessageCircleQuestion", heading: "Question", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: true, pending: true}, lineage: "Question"},
  "todowrite": {label: ["todowrite", "todowrite"], icon: "Wrench", heading: "Todowrite", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Todowrite"},
  "todoread": {label: ["todoread", "todoread"], icon: "Wrench", heading: "Todoread", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Todoread"},
  "codesearch": {label: ["codesearch", "codesearch"], icon: "Wrench", heading: "Codesearch", turns: {shell: false, file: false, created: false}, fold: {category: "search", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Codesearch"},
  "lsp": {label: ["lsp", "lsp"], icon: "Wrench", heading: "Lsp", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Lsp"},
  "multiedit": {label: ["multiedit", "multiedit"], icon: "Wrench", heading: "Multiedit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Multiedit"},
  "notebookedit": {label: ["notebookedit", "notebookedit"], icon: "Wrench", heading: "Notebookedit", turns: {shell: false, file: true, created: true}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Notebookedit"},
  "strreplaceeditor": {label: ["strreplaceeditor", "strreplaceeditor"], icon: "Wrench", heading: "Strreplaceeditor", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Strreplaceeditor"},
  "terminal": {label: ["terminal", "terminal"], icon: "Wrench", heading: "Terminal", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Terminal"},
  "Read": {label: ["Read", "Read"], icon: "Wrench", heading: "Read", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Read"},
  "Edit": {label: ["Edit", "Edit"], icon: "Wrench", heading: "Edit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Edit"},
  "MultiEdit": {label: ["MultiEdit", "MultiEdit"], icon: "Wrench", heading: "MultiEdit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "MultiEdit"},
  "NotebookEdit": {label: ["NotebookEdit", "NotebookEdit"], icon: "Wrench", heading: "NotebookEdit", turns: {shell: false, file: true, created: true}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "NotebookEdit"},
  "Bash": {label: ["Bash", "Bash"], icon: "Wrench", heading: "Bash", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "Bash"},
  "Task": {label: ["Task", "Task"], icon: "Wrench", heading: "Task", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Task"},
  "TodoWrite": {label: ["TodoWrite", "TodoWrite"], icon: "Wrench", heading: "TodoWrite", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "TodoWrite"},
  "WebFetch": {label: ["WebFetch", "WebFetch"], icon: "Wrench", heading: "WebFetch", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "WebFetch"},
  "Agent": {label: ["Agent", "Agent"], icon: "Wrench", heading: "Agent", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Agent"},
  "LS": {label: ["LS", "LS"], icon: "Wrench", heading: "LS", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "LS"},
  "shell": {label: ["shell", "Check the panel"], icon: "Terminal", heading: "Shell", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "Shell"},
  "execute": {label: ["execute", "await page.open()"], icon: "Code", heading: "Code", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Execute"},
  "subagent": {label: ["subagent", "explore · Check the panel"], icon: "GitBranch", heading: "Subagent", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Subagent"},
  "Subagent": {label: ["Subagent", "Subagent"], icon: "Wrench", heading: "Subagent", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Subagent"},
  "SHELL": {label: ["SHELL", "SHELL"], icon: "Wrench", heading: "SHELL", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "SHELL"},
  "fleet_app_start": {label: ["fleet_app_start", "Panel · dotnet test"], icon: "AppWindow", heading: "Run app", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_app_start"},
  "fleet_browser_open": {label: ["fleet_browser_open", "Panel · dotnet test"], icon: "Globe", heading: "Open page", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_browser_open"},
  "fleet_browser_screenshot": {label: ["screenshot (desktop)", "screenshot (desktop)"], icon: "Camera", heading: "Screenshot", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_browser_screenshot"},
  "fleet_browser_read": {label: ["page", "page"], icon: "Globe", heading: "Read page", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_browser_read"},
  "fleet_browser_act": {label: ["act", "act https://example.test/docs"], icon: "Globe", heading: "Use page", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_browser_act"},
  "fleet_page_show": {label: ["fleet_page_show", "Panel"], icon: "Layout", heading: "Show page", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_page_show"},
  "fleet_walkthrough_show": {label: ["fleet_walkthrough_show", "Panel"], icon: "BookOpen", heading: "Walkthrough", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_walkthrough_show"},
  "fleet_message": {label: ["fleet_message", "fleet_message"], icon: "Send", heading: "Message session", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_message"},
  "fleet_session_read": {label: ["fleet_session_read", "fleet_session_read"], icon: "MessagesSquare", heading: "Read session", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_session_read"},
  "fleet_machine_list": {label: ["fleet_machine_list", "fleet_machine_list"], icon: "Server", heading: "List machines", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_machine_list"},
  "fleet_session_start": {label: ["fleet_session_start", "fleet_session_start"], icon: "MonitorUp", heading: "Start session on a machine", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_session_start"},
  "fleet_memory_save": {label: ["remember", "remember"], icon: "Lightbulb", heading: "Remember", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_memory_save"},
  "fleet_memory_forget": {label: ["forget note", "forget note"], icon: "LightbulbOff", heading: "Forget note", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_memory_forget"},
  "fleet_step_done": {label: ["fleet_step_done", "fleet_step_done"], icon: "CheckCircle2", heading: "Step done", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_step_done"},
  "fleet_canvas_open": {label: ["fleet_canvas_open", "fleet_canvas_open"], icon: "Wrench", heading: "Fleet_canvas_open", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_canvas_open"},
  "mcp__fleet__fleet_page_show": {label: ["mcp__fleet__fleet_page_show", "mcp__fleet__fleet_page_show"], icon: "Wrench", heading: "Mcp__fleet__fleet_page_show", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_page_show"},
  "mcp__acme__lookup_order": {label: ["mcp__acme__lookup_order", "mcp__acme__lookup_order"], icon: "Wrench", heading: "Mcp__acme__lookup_order", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Lookup_order"},
  "frobnicate": {label: ["frobnicate", "frobnicate"], icon: "Wrench", heading: "Frobnicate", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Frobnicate"},
  "": {label: ["", ""], icon: "Wrench", heading: "", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: ""},
};

describe("how the client classifies tools today", () => {
  it.each(NAMES)("%j", (name) => {
    expect(rowFor(name)).toEqual(TODAY[name]);
  });

  // DRIFT, pinned as it is. B2 (the registry migration) is meant to change these on purpose.
  describe("drift", () => {
    it("todowrite has no label and no icon anywhere: a Wrench and 'Todowrite' (the server sends Claude Code's TodoWrite lowercased)", () => {
      expect(TODAY.todowrite.icon).toBe("Wrench");
      expect(TODAY.todowrite.heading).toBe("Todowrite");
      expect(TODAY.todowrite.label).toEqual(["todowrite", "todowrite"]);
      expect(TODAY.todoread.icon).toBe("Wrench");
    });

    it("`task` has no label builder (its card says 'task'), though its icon and heading exist; `subagent` has all three", () => {
      expect(TODAY.task.label).toEqual(["task", "task"]);
      expect(TODAY.subagent.label[1]).toBe("explore · Check the panel");
    });

    it("edit-like tools: icons know only edit and write, turns knows six names, fold-steps now agrees with it", () => {
      // turns.ts counts these as file writes; fold-steps used to file them under 'other' and now says edit (the registry).
      for (const name of ["notebookedit", "strreplaceeditor"]) {
        expect(TODAY[name].turns.file).toBe(true);
        expect(TODAY[name].fold.category).toBe("edit");
      }
      // multiedit, patch and apply_patch are edits in both, yet nothing gives them a label or icon of their own.
      for (const name of ["multiedit", "patch", "apply_patch"]) {
        expect(TODAY[name].turns.file).toBe(true);
        expect(TODAY[name].fold.category).toBe("edit");
        expect(TODAY[name].icon).toBe("Wrench");
      }
      // Only `write` and `notebookedit` count as creating a file.
      expect(TODAY.write.turns.created).toBe(true);
      expect(TODAY.notebookedit.turns.created).toBe(true);
      expect(TODAY.edit.turns.created).toBe(false);
    });

    it("shell-like tools: `terminal` is a shell for turns and now fold-steps, `execute` is Code for icons and nothing for the others (the server counts it as a shell)", () => {
      expect(TODAY.terminal.turns.shell).toBe(true);
      expect(TODAY.terminal.fold.category).toBe("run");
      expect(TODAY.execute.icon).toBe("Code");
      expect(TODAY.execute.turns.shell).toBe(false);
      expect(TODAY.execute.fold.category).toBe("other");
      expect(TODAY.execute.prShell).toBe(false);
      // bash and shell agree everywhere.
      for (const name of ["bash", "shell"]) {
        expect(TODAY[name].turns.shell).toBe(true);
        expect(TODAY[name].fold.category).toBe("run");
        expect(TODAY[name].prShell).toBe(true);
      }
    });

    it("read, search and web tools: fold-steps files list, webfetch and LS under read, websearch and codesearch under search; icons and labels know fewer", () => {
      expect(TODAY.list.fold.category).toBe("read");
      expect(TODAY.list.icon).toBe("Wrench");
      expect(TODAY.webfetch.fold.category).toBe("read");
      expect(TODAY.websearch.fold.category).toBe("search");
      expect(TODAY.websearch.icon).toBe("Search");
      expect(TODAY.codesearch.fold.category).toBe("search");
      expect(TODAY.codesearch.icon).toBe("Wrench");
      // lsp is a read now (the registry); skill, question, todo* and the fleet tools stay 'other' in fold-steps.
      expect(TODAY.lsp.fold.category).toBe("read");
      for (const name of ["skill", "question", "todoread", "fleet_message"]) expect(TODAY[name].fold.category).toBe("other");
    });

    it("case: fold-steps, the subagent checks, pr-utils and turns lowercase the name; labels, icons and the pattern/question checks match it exactly", () => {
      // Lowercased by fold-steps, isSubagentTool, isBashTool and turns.
      expect(TODAY.Read.fold.category).toBe("read");
      expect(TODAY.Task.fold.subagent).toBe(true);
      expect(TODAY.Task.card.subagent).toBe(true);
      expect(TODAY.SHELL.prShell).toBe(true);
      expect(TODAY.NotebookEdit.turns.file).toBe(true);
      expect(TODAY.Bash.turns.shell).toBe(true);
      // Exact: the label, the icon and the heading ('Wrench' and the raw name for Claude Code's own casing).
      expect(TODAY.Read.icon).toBe("Wrench");
      expect(TODAY.Bash.label).toEqual(["Bash", "Bash"]);
      // `Agent` is the Claude Code subagent tool; the registry makes it an alias of task, so fold-steps now knows it
      // (the desktop's tool card still does not, until it moves onto the registry).
      expect(TODAY.Agent.fold.subagent).toBe(true);
      expect(TODAY.Agent.card.subagent).toBe(false);
    });

    it("the subagent set is defined twice (fold-steps and the tool card) and agrees: task and subagent, either case", () => {
      for (const name of ["task", "subagent", "Task", "Subagent"]) {
        expect(TODAY[name].fold.subagent).toBe(true);
        expect(TODAY[name].card.subagent).toBe(true);
      }
    });

    it("the pattern pill is glob and grep, exactly cased, on the card; fold-steps lowercases", () => {
      expect(TODAY.glob.card.pattern).toBe(true);
      expect(TODAY.grep.card.pattern).toBe(true);
      expect(TODAY.websearch.card.pattern).toBe(false);
    });

    it("Fleet's titled tools take their title from Fleet's answer; the memory and browser read/act tools do not", () => {
      for (const name of ["fleet_app_start", "fleet_browser_open", "fleet_browser_screenshot", "fleet_page_show", "fleet_walkthrough_show", "fleet_message", "fleet_session_read", "fleet_machine_list", "fleet_session_start"]) {
        expect(TODAY[name].card.titledFromAnswer).toBe(true);
      }
      for (const name of ["fleet_browser_read", "fleet_browser_act", "fleet_memory_save", "fleet_step_done"]) {
        expect(TODAY[name].card.titledFromAnswer).toBe(false);
      }
    });

    it("`mcp__` is stripped by the lineage panel only: the server strips Fleet's own server, every other site sees the full name", () => {
      expect(TODAY["mcp__fleet__fleet_page_show"].lineage).toBe("Fleet_page_show");
      expect(TODAY["mcp__fleet__fleet_page_show"].icon).toBe("Wrench");
      expect(TODAY["mcp__acme__lookup_order"].lineage).toBe("Lookup_order");
      expect(TODAY["mcp__acme__lookup_order"].heading).toBe("Mcp__acme__lookup_order");
    });

    it("the question checks match `question` exactly", () => {
      expect(TODAY.question.question).toEqual({ part: true, pending: true });
      expect(TODAY.Task.question).toEqual({ part: false, pending: false });
    });
  });
});
