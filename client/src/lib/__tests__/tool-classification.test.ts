/**
 * How the client classifies tools: one table of names, read through every site that classifies a tool.
 *
 * Tool knowledge used to live in a dozen places (labels, icons, turns, the phone's fold-steps, the tool card, pr-utils,
 * the question checks, the lineage panel) and they had drifted apart. They all read the registry in lib/tools now; this
 * pins what each one says about a common table of names, so they cannot drift again.
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
  "fleet_session_start", "fleet_memory_save", "fleet_memory_forget", "fleet_mod_write", "fleet_mod_check",
  "fleet_mod_reload", "fleet_mod_test", "fleet_mod_keep", "fleet_mod_list", "fleet_step_done", "fleet_canvas_open",
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
 * What every site says, one row per name. The "agreement" notes under the table name what used to differ.
 * Icons are lucide names (an alias such as CheckCircle2 for CircleCheck is the same component).
 */
const TODAY: Record<string, Row> = {
  "read": {label: ["read", "src/widgets/panel.ts"], icon: "FileText", heading: "Read", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Read"},
  "write": {label: ["write", "src/widgets/panel.ts"], icon: "Pencil", heading: "Write", turns: {shell: false, file: true, created: true}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Write"},
  "edit": {label: ["edit", "src/widgets/panel.ts"], icon: "Pencil", heading: "Edit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Edit"},
  "patch": {label: ["patch", "src/widgets/panel.ts"], icon: "Pencil", heading: "Patch", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Patch"},
  "apply_patch": {label: ["apply_patch", "src/widgets/panel.ts"], icon: "Pencil", heading: "Patch", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Apply_patch"},
  "list": {label: ["list", "src/widgets/panel.ts"], icon: "Folder", heading: "List", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "List"},
  "glob": {label: ["glob", "*.ts"], icon: "Search", heading: "Glob", turns: {shell: false, file: false, created: false}, fold: {category: "search", subagent: false, pattern: true}, card: {subagent: false, pattern: true, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Glob"},
  "grep": {label: ["grep", "*.ts"], icon: "Search", heading: "Grep", turns: {shell: false, file: false, created: false}, fold: {category: "search", subagent: false, pattern: true}, card: {subagent: false, pattern: true, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Grep"},
  "bash": {label: ["bash", "Check the panel"], icon: "Terminal", heading: "Bash", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "Bash"},
  "webfetch": {label: ["webfetch", "https://example.test/docs"], icon: "Globe", heading: "Web Fetch", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Webfetch"},
  "websearch": {label: ["websearch", "reconnect backoff"], icon: "Search", heading: "Web Search", turns: {shell: false, file: false, created: false}, fold: {category: "search", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Websearch"},
  "task": {label: ["task", "task"], icon: "GitBranch", heading: "Task", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Task"},
  "skill": {label: ["skill", "fleet-run"], icon: "Layers", heading: "Skill", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Skill"},
  "question": {label: ["question", "Which folder?"], icon: "MessageCircleQuestion", heading: "Question", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: true, pending: true}, lineage: "Question"},
  "todowrite": {label: ["todowrite", "todowrite"], icon: "ListTodo", heading: "Update todos", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Todowrite"},
  "todoread": {label: ["todoread", "todoread"], icon: "ListChecks", heading: "Read todos", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Todoread"},
  "codesearch": {label: ["codesearch", "codesearch"], icon: "Search", heading: "Codesearch", turns: {shell: false, file: false, created: false}, fold: {category: "search", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Codesearch"},
  "lsp": {label: ["lsp", "src/widgets/panel.ts"], icon: "Wrench", heading: "Lsp", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Lsp"},
  "multiedit": {label: ["multiedit", "src/widgets/panel.ts"], icon: "Pencil", heading: "Edit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Multiedit"},
  "notebookedit": {label: ["notebookedit", "src/widgets/panel.ts"], icon: "Pencil", heading: "Notebook Edit", turns: {shell: false, file: true, created: true}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Notebookedit"},
  "strreplaceeditor": {label: ["strreplaceeditor", "src/widgets/panel.ts"], icon: "Pencil", heading: "Edit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Strreplaceeditor"},
  "terminal": {label: ["terminal", "Check the panel"], icon: "Terminal", heading: "Shell", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "Terminal"},
  "Read": {label: ["Read", "src/widgets/panel.ts"], icon: "FileText", heading: "Read", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Read"},
  "Edit": {label: ["Edit", "src/widgets/panel.ts"], icon: "Pencil", heading: "Edit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Edit"},
  "MultiEdit": {label: ["MultiEdit", "src/widgets/panel.ts"], icon: "Pencil", heading: "Edit", turns: {shell: false, file: true, created: false}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "MultiEdit"},
  "NotebookEdit": {label: ["NotebookEdit", "src/widgets/panel.ts"], icon: "Pencil", heading: "Notebook Edit", turns: {shell: false, file: true, created: true}, fold: {category: "edit", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "NotebookEdit"},
  "Bash": {label: ["Bash", "Check the panel"], icon: "Terminal", heading: "Bash", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "Bash"},
  "Task": {label: ["Task", "Task"], icon: "GitBranch", heading: "Task", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Task"},
  "TodoWrite": {label: ["TodoWrite", "TodoWrite"], icon: "ListTodo", heading: "Update todos", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "TodoWrite"},
  "WebFetch": {label: ["WebFetch", "https://example.test/docs"], icon: "Globe", heading: "Web Fetch", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "WebFetch"},
  "Agent": {label: ["Agent", "Agent"], icon: "GitBranch", heading: "Task", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Agent"},
  "LS": {label: ["LS", "src/widgets/panel.ts"], icon: "Folder", heading: "List", turns: {shell: false, file: false, created: false}, fold: {category: "read", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "LS"},
  "shell": {label: ["shell", "Check the panel"], icon: "Terminal", heading: "Shell", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "Shell"},
  "execute": {label: ["execute", "await page.open()"], icon: "Code", heading: "Code", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Execute"},
  "subagent": {label: ["subagent", "explore · Check the panel"], icon: "GitBranch", heading: "Subagent", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Subagent"},
  "Subagent": {label: ["Subagent", "explore · Check the panel"], icon: "GitBranch", heading: "Subagent", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: true, pattern: false}, card: {subagent: true, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Subagent"},
  "SHELL": {label: ["SHELL", "Check the panel"], icon: "Terminal", heading: "Shell", turns: {shell: true, file: false, created: false}, fold: {category: "run", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: true, question: {part: false, pending: false}, lineage: "SHELL"},
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
  "fleet_mod_write": {label: ["write mod", "fleet-run"], icon: "Pencil", heading: "Write mod", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_mod_write"},
  "fleet_mod_check": {label: ["check mod", "fleet-run"], icon: "CheckCircle2", heading: "Check mod", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_mod_check"},
  "fleet_mod_reload": {label: ["reload mod", "fleet-run"], icon: "RefreshCw", heading: "Reload mod", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_mod_reload"},
  "fleet_mod_test": {label: ["test mod", "fleet-run"], icon: "FlaskConical", heading: "Test mod", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_mod_test"},
  "fleet_mod_keep": {label: ["keep mod", "fleet-run"], icon: "PackageCheck", heading: "Keep mod", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_mod_keep"},
  "fleet_mod_list": {label: ["list mods", "list mods"], icon: "ListChecks", heading: "List mods", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_mod_list"},
  "fleet_step_done": {label: ["fleet_step_done", "fleet_step_done"], icon: "CheckCircle2", heading: "Step done", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_step_done"},
  "fleet_canvas_open": {label: ["fleet_canvas_open", "fleet_canvas_open"], icon: "Wrench", heading: "Fleet_canvas_open", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_canvas_open"},
  "mcp__fleet__fleet_page_show": {label: ["mcp__fleet__fleet_page_show", "Panel"], icon: "Layout", heading: "Show page", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: true}, prShell: false, question: {part: false, pending: false}, lineage: "Fleet_page_show"},
  "mcp__acme__lookup_order": {label: ["mcp__acme__lookup_order", "mcp__acme__lookup_order"], icon: "Wrench", heading: "Mcp__acme__lookup_order", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Lookup_order"},
  "frobnicate": {label: ["frobnicate", "frobnicate"], icon: "Wrench", heading: "Frobnicate", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: "Frobnicate"},
  "": {label: ["", ""], icon: "Wrench", heading: "", turns: {shell: false, file: false, created: false}, fold: {category: "other", subagent: false, pattern: false}, card: {subagent: false, pattern: false, titledFromAnswer: false}, prShell: false, question: {part: false, pending: false}, lineage: ""},
};

describe("how the client classifies tools", () => {
  it.each(NAMES)("%j", (name) => {
    expect(rowFor(name)).toEqual(TODAY[name]);
  });

  // Where the sites used to disagree, and what the registry (lib/tools) now says for all of them, desktop and phone.
  describe("agreement", () => {
    it("todowrite has the ListTodo icon, the heading 'Update todos' and 'N todos' as its label (the server sends Claude Code's TodoWrite lowercased)", () => {
      for (const name of ["todowrite", "TodoWrite"]) {
        expect(TODAY[name].icon).toBe("ListTodo");
        expect(TODAY[name].heading).toBe("Update todos");
      }
      expect(TODAY.todoread.icon).toBe("ListChecks");
      expect(getToolLabel("todowrite", { todos: [{ content: "a" }, { content: "b" }, { content: "c" }] })).toBe("3 todos");
      expect(getToolLabel("TodoWrite", { todos: [{ content: "a" }] })).toBe("1 todo");
      expect(getToolLabel("todowrite", { todos: [] })).toBe("todowrite");
    });

    it("`task` has no label builder (its card says 'task'), though its icon and heading exist; `subagent` has all three", () => {
      expect(TODAY.task.label).toEqual(["task", "task"]);
      expect(TODAY.subagent.label[1]).toBe("explore · Check the panel");
    });

    it("edit-like tools share the Pencil; only `write` and `notebookedit` count as creating a file", () => {
      for (const name of ["notebookedit", "strreplaceeditor", "multiedit", "patch", "apply_patch"]) {
        expect(TODAY[name].turns.file).toBe(true);
        expect(TODAY[name].fold.category).toBe("edit");
        expect(TODAY[name].icon).toBe("Pencil");
      }
      expect(TODAY.write.turns.created).toBe(true);
      expect(TODAY.notebookedit.turns.created).toBe(true);
      expect(TODAY.edit.turns.created).toBe(false);
      // multiedit and strreplaceeditor are `edit` under another name: they label by their file.
      expect(TODAY.multiedit.label[1]).toBe("src/widgets/panel.ts");
      expect(TODAY.apply_patch.heading).toBe("Patch");
      // The phone shows the file for these, so the desktop label is the file too.
      expect(TODAY.notebookedit.label[1]).toBe("src/widgets/panel.ts");
      expect(TODAY.list.label[1]).toBe("src/widgets/panel.ts");
      expect(getToolLabel("notebookedit", { notebook_path: "notebooks/demo.ipynb" })).toBe("notebooks/demo.ipynb");
      expect(getToolLabel("list", { path: "src" })).toBe("src");
    });

    it("shell-like tools: `terminal` is a shell everywhere; `execute` keeps the Code icon and is not a command in turns, fold-steps or pr-utils", () => {
      expect(TODAY.terminal.turns.shell).toBe(true);
      expect(TODAY.terminal.fold.category).toBe("run");
      expect(TODAY.terminal.prShell).toBe(true);
      expect(TODAY.terminal.icon).toBe("Terminal");
      expect(TODAY.execute.icon).toBe("Code");
      expect(TODAY.execute.turns.shell).toBe(false);
      expect(TODAY.execute.fold.category).toBe("other");
      expect(TODAY.execute.prShell).toBe(false);
      for (const name of ["bash", "shell"]) {
        expect(TODAY[name].turns.shell).toBe(true);
        expect(TODAY[name].fold.category).toBe("run");
        expect(TODAY[name].prShell).toBe(true);
      }
    });

    it("read, search and web tools: list and LS get a Folder, codesearch a Search; fold-steps files list, webfetch under read, websearch and codesearch under search", () => {
      expect(TODAY.list.fold.category).toBe("read");
      expect(TODAY.list.icon).toBe("Folder");
      expect(TODAY.LS.icon).toBe("Folder");
      expect(TODAY.webfetch.fold.category).toBe("read");
      expect(TODAY.websearch.fold.category).toBe("search");
      expect(TODAY.websearch.icon).toBe("Search");
      expect(TODAY.codesearch.fold.category).toBe("search");
      expect(TODAY.codesearch.icon).toBe("Search");
      expect(TODAY.lsp.fold.category).toBe("read");
      for (const name of ["skill", "question", "todoread", "fleet_message"]) expect(TODAY[name].fold.category).toBe("other");
    });

    it("case does not matter anywhere: Claude Code's own casing gets the same label, icon and heading as the lowercase name", () => {
      expect(TODAY.Read.icon).toBe("FileText");
      expect(TODAY.Bash.label).toEqual(["Bash", "Check the panel"]);
      expect(TODAY.Bash.icon).toBe("Terminal");
      expect(TODAY.SHELL.heading).toBe("Shell");
      expect(TODAY.Task.fold.subagent).toBe(true);
      expect(TODAY.Task.card.subagent).toBe(true);
      expect(TODAY.SHELL.prShell).toBe(true);
      expect(TODAY.NotebookEdit.turns.file).toBe(true);
      expect(TODAY.Bash.turns.shell).toBe(true);
      // The unknown name keeps its own casing when nothing is known.
      expect(TODAY.frobnicate.heading).toBe("Frobnicate");
    });

    it("`Agent` is a subagent on the desktop card too, drawn as a Task", () => {
      expect(TODAY.Agent.fold.subagent).toBe(true);
      expect(TODAY.Agent.card.subagent).toBe(true);
      expect(TODAY.Agent.icon).toBe("GitBranch");
      expect(TODAY.Agent.heading).toBe("Task");
    });

    it("the subagent set: task, agent and subagent, either case", () => {
      for (const name of ["task", "subagent", "Task", "Subagent", "Agent"]) {
        expect(TODAY[name].fold.subagent).toBe(true);
        expect(TODAY[name].card.subagent).toBe(true);
      }
    });

    it("the pattern pill is glob and grep, in any case", () => {
      expect(TODAY.glob.card.pattern).toBe(true);
      expect(TODAY.grep.card.pattern).toBe(true);
      expect(TODAY.websearch.card.pattern).toBe(false);
      expect(toToolCardItem(part("Grep", { status: "completed", input: RICH_INPUT })).isPatternTool).toBe(true);
    });

    it("Fleet's titled tools take their title from Fleet's answer; the memory and browser read/act tools do not", () => {
      for (const name of ["fleet_app_start", "fleet_browser_open", "fleet_browser_screenshot", "fleet_page_show", "fleet_walkthrough_show", "fleet_message", "fleet_session_read", "fleet_machine_list", "fleet_session_start"]) {
        expect(TODAY[name].card.titledFromAnswer).toBe(true);
      }
      for (const name of ["fleet_browser_read", "fleet_browser_act", "fleet_memory_save", "fleet_step_done"]) {
        expect(TODAY[name].card.titledFromAnswer).toBe(false);
      }
    });

    it("`mcp__fleet__x` is Fleet's tool x everywhere; another server's tool is unknown and shown as its own name", () => {
      expect(TODAY["mcp__fleet__fleet_page_show"].lineage).toBe("Fleet_page_show");
      expect(TODAY["mcp__fleet__fleet_page_show"].icon).toBe("Layout");
      expect(TODAY["mcp__fleet__fleet_page_show"].heading).toBe("Show page");
      expect(TODAY["mcp__fleet__fleet_page_show"].card.titledFromAnswer).toBe(true);
      expect(TODAY["mcp__acme__lookup_order"].lineage).toBe("Lookup_order");
      expect(TODAY["mcp__acme__lookup_order"].icon).toBe("Wrench");
      expect(TODAY["mcp__acme__lookup_order"].heading).toBe("Mcp__acme__lookup_order");
    });

    it("the question checks match `question` exactly", () => {
      expect(TODAY.question.question).toEqual({ part: true, pending: true });
      expect(TODAY.Task.question).toEqual({ part: false, pending: false });
    });
  });
});
