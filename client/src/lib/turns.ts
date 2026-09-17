/**
 * Turns: the rounds of a session, derived from its messages.
 *
 * A *turn* here is a round — one prompt of yours, plus everything the agent did until it stopped.
 * (Not `turn.started`/`turn.ended`, which are the harness's steps inside a single assistant message.)
 * Rounds come from the message history, so they survive a reload and reuse the paging the
 * conversation already does.
 *
 * What a turn reports is **the edits it made**, not the state of the file afterwards: turn 10 still
 * shows what turn 10 wrote even when turn 18 has since written over the same lines. That reading
 * comes straight off the turn's own tool calls, with no snapshots, so it behaves the same on every
 * harness.
 */

import { toolDiffLines, toolDiffText, toolInput, toolStatus } from "@/components/session/activity-stream-tool-card";
import type { AccumulatedMessage, AccumulatedToolPart, DelegationDto } from "@/lib/client-types";
import { parseDiffLines, type DiffLine } from "@/lib/diff-parser";

/** A file a turn wrote, with the lines that turn wrote to it. */
export interface TurnFile {
  path: string;
  /** The file's own name, for the strong half of the row. */
  name: string;
  /** Its directory, dimmed after the name. */
  dir: string;
  additions: number;
  deletions: number;
  /** The turn created the file (as far as its tool calls can tell). */
  created: boolean;
  /** The lines this turn wrote, in call order; empty when the harness sent no diff to derive them from. */
  diff: readonly DiffLine[];
}

/** A subagent started during the turn. */
export interface TurnDelegation {
  id: string;
  title: string;
  status: DelegationDto["status"];
  childSessionId: string | null;
}

/** A shell command the turn ran, for the "Also ran" line. */
export interface TurnCommand {
  label: string;
  /** true finished, false failed, null still running or unknown. */
  ok: boolean | null;
}

export interface SessionTurn {
  /** The id of the round's first message — also what "Show this turn in the chat" scrolls to. */
  id: string;
  /** 1 for the oldest *loaded* round. Older rounds shift it, which is why `hasOlder` is shown. */
  number: number;
  /** First line of the prompt, for the row. */
  prompt: string;
  /** The whole prompt, for the row's title attribute. */
  promptFull: string;
  /** False for a round whose prompt is older than the loaded history. */
  hasPrompt: boolean;
  /** The last model that answered in the round. */
  modelId?: string;
  /** The round ran on more than one model; the row says so. */
  modelChanged: boolean;
  agent?: string;
  startedAt?: number;
  endedAt?: number;
  durationMs: number | null;
  tokensInput: number;
  tokensOutput: number;
  cost: number;
  files: readonly TurnFile[];
  additions: number;
  deletions: number;
  /** Every tool call in the round, including reads. */
  toolCount: number;
  /** Calls that wrote a file. */
  editCount: number;
  commands: readonly TurnCommand[];
  delegations: readonly TurnDelegation[];
  /** The round ran tools but wrote nothing — it still gets a row, and says so. */
  readOnly: boolean;
}

/** Tools that write a file, across harnesses: OpenCode lowercases, Claude Code does not. */
const WRITE_TOOLS = new Set(["edit", "write", "patch", "applypatch", "multiedit", "notebookedit", "strreplaceeditor"]);
const CREATE_TOOLS = new Set(["write", "notebookedit"]);
const SHELL_TOOLS = new Set(["bash", "shell", "terminal"]);

/** "apply_patch" → "applypatch", "MultiEdit" → "multiedit". */
function normalizeToolName(tool: string): string {
  return tool.toLowerCase().replace(/[^a-z0-9]/g, "");
}

function stringField(input: Record<string, unknown> | null, ...keys: readonly string[]): string | undefined {
  for (const key of keys) {
    const value = input?.[key];
    if (typeof value === "string" && value.trim()) return value;
  }
  return undefined;
}

/**
 * Parsing a call's diff is the expensive part and a call never changes once it completed, so the
 * result is kept per call id. The cache is bounded: a long session is thousands of calls.
 */
const MAX_CACHED_CALLS = 4000;
const fileEditCache = new Map<string, readonly TurnFileEdit[]>();

interface TurnFileEdit {
  path: string;
  additions: number;
  deletions: number;
  created: boolean;
  diff: readonly DiffLine[];
}

function cacheFileEdits(key: string, edits: readonly TurnFileEdit[]): readonly TurnFileEdit[] {
  if (fileEditCache.size >= MAX_CACHED_CALLS) {
    const oldest = fileEditCache.keys().next();
    if (!oldest.done) fileEditCache.delete(oldest.value);
  }
  fileEditCache.set(key, edits);
  return edits;
}

/** Only for tests: forget what earlier calls parsed to. */
export function clearTurnDerivationCache(): void {
  fileEditCache.clear();
}

// ─── Diff reading ───────────────────────────────────────────────────────────

/**
 * Parse a unified diff (what most harnesses attach to an edit) into lines. Hunk headers give the
 * line numbers; "--- /dev/null" means the file is new.
 */
export function parseUnifiedDiff(patch: string): { lines: DiffLine[]; created: boolean; paths: string[] } {
  const lines: DiffLine[] = [];
  const paths: string[] = [];
  let created = false;
  let oldLine = 0;
  let newLine = 0;

  for (const raw of patch.split("\n")) {
    const hunk = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(raw);
    if (hunk) {
      oldLine = Number(hunk[1]);
      newLine = Number(hunk[2]);
      continue;
    }

    if (raw.startsWith("--- ")) {
      if (raw.slice(4).trim() === "/dev/null") created = true;
      continue;
    }

    if (raw.startsWith("+++ ")) {
      const path = stripPatchPathPrefix(raw.slice(4).trim());
      if (path && path !== "/dev/null") paths.push(path);
      continue;
    }

    // OpenCode's apply_patch names its files in its own header.
    const applyPatch = /^\*\*\* (Add|Update|Delete) File: (.+)$/.exec(raw);
    if (applyPatch) {
      if (applyPatch[1] === "Add") created = true;
      paths.push(applyPatch[2]!.trim());
      continue;
    }

    if (raw.startsWith("diff ") || raw.startsWith("index ") || raw.startsWith("\\ ")) continue;

    if (raw.startsWith("+")) {
      lines.push({ type: "add", content: raw.slice(1), newLineNumber: newLine });
      newLine += 1;
      continue;
    }

    if (raw.startsWith("-")) {
      lines.push({ type: "remove", content: raw.slice(1), oldLineNumber: oldLine });
      oldLine += 1;
      continue;
    }

    if (raw.startsWith(" ")) {
      lines.push({ type: "context", content: raw.slice(1), oldLineNumber: oldLine, newLineNumber: newLine });
      oldLine += 1;
      newLine += 1;
    }
  }

  return { lines, created, paths };
}

function stripPatchPathPrefix(path: string): string {
  const withoutTab = path.split("\t")[0] ?? path;
  return withoutTab.replace(/^[ab]\//, "");
}

function countChanges(lines: readonly DiffLine[]): { additions: number; deletions: number } {
  let additions = 0;
  let deletions = 0;
  for (const line of lines) {
    if (line.type === "add") additions += 1;
    else if (line.type === "remove") deletions += 1;
  }
  return { additions, deletions };
}

function linesOf(content: string): string[] {
  const split = content.split("\n");
  if (split.at(-1) === "") split.pop();
  return split;
}

/** Every file a completed write call touched, with the lines it wrote to each. */
function fileEditsOf(part: AccumulatedToolPart): readonly TurnFileEdit[] {
  const tool = normalizeToolName(part.tool);
  if (!WRITE_TOOLS.has(tool)) return [];
  // A call that failed or hasn't finished wrote nothing.
  if (toolStatus(part) !== "completed") return [];

  const cacheKey = `${part.partId}|${part.callId}|${tool}`;
  const cached = fileEditCache.get(cacheKey);
  if (cached) return cached;

  const input = toolInput(part);
  const declaredPath = stringField(input, "filePath", "file_path", "path", "file", "notebook_path");

  // 1. Diff lines the harness sent as an array.
  const attached = toolDiffLines(part);
  if (attached.length > 0 && declaredPath) {
    const { additions, deletions } = countChanges(attached);
    return cacheFileEdits(cacheKey, [{
      path: declaredPath,
      additions,
      deletions,
      created: CREATE_TOOLS.has(tool) && deletions === 0,
      diff: attached,
    }]);
  }

  // 2. A unified diff as text, on the state or its metadata — one call can carry several files.
  const patchText = toolDiffText(part) ?? stringField(input, "patchText", "patch", "diff");
  if (patchText) {
    const edits = splitPatchByFile(patchText, declaredPath);
    if (edits.length > 0) return cacheFileEdits(cacheKey, edits);
  }

  // 3. Nothing but the input: work the lines out from what was asked for.
  if (!declaredPath) return cacheFileEdits(cacheKey, []);

  const newString = stringField(input, "newString", "new_string");
  const oldString = stringField(input, "oldString", "old_string");
  if (newString !== undefined || oldString !== undefined) {
    const diff = parseDiffLines(oldString ?? "", newString ?? "").filter((line) => line.type !== "context");
    const { additions, deletions } = countChanges(diff);
    return cacheFileEdits(cacheKey, [{ path: declaredPath, additions, deletions, created: false, diff }]);
  }

  const content = stringField(input, "content", "text", "newContent", "new_content");
  if (content !== undefined) {
    const diff: DiffLine[] = linesOf(content).map((line, index) => ({
      type: "add" as const,
      content: line,
      newLineNumber: index + 1,
    }));
    return cacheFileEdits(cacheKey, [{
      path: declaredPath,
      additions: diff.length,
      deletions: 0,
      created: CREATE_TOOLS.has(tool),
      diff,
    }]);
  }

  // A write we can see happened but can't read: the file is still worth naming.
  return cacheFileEdits(cacheKey, [{ path: declaredPath, additions: 0, deletions: 0, created: false, diff: [] }]);
}

/** Split a multi-file patch into one edit per file. */
function splitPatchByFile(patchText: string, declaredPath: string | undefined): TurnFileEdit[] {
  const sections: { header: string; body: string[] }[] = [];
  let current: { header: string; body: string[] } | null = null;

  for (const raw of patchText.split("\n")) {
    const isHeader = raw.startsWith("--- ") || /^\*\*\* (Add|Update|Delete) File: /.test(raw);
    if (isHeader && current && current.body.length > 0) {
      sections.push(current);
      current = null;
    }
    if (!current) current = { header: raw, body: [] };
    current.body.push(raw);
  }
  if (current) sections.push(current);

  const edits: TurnFileEdit[] = [];
  for (const section of sections) {
    const parsed = parseUnifiedDiff(section.body.join("\n"));
    if (parsed.lines.length === 0 && parsed.paths.length === 0) continue;
    const { additions, deletions } = countChanges(parsed.lines);
    const path = parsed.paths[0] ?? declaredPath;
    if (!path) continue;
    edits.push({ path, additions, deletions, created: parsed.created, diff: parsed.lines });
  }

  return edits;
}

// ─── Rounds ─────────────────────────────────────────────────────────────────

interface Round {
  messages: AccumulatedMessage[];
  hasPrompt: boolean;
}

/** Split messages (oldest first) into rounds: a user message and everything until the next one. */
function toRounds(messages: readonly AccumulatedMessage[]): Round[] {
  const rounds: Round[] = [];

  for (const message of messages) {
    if (message.role === "user" || rounds.length === 0) {
      rounds.push({ messages: [message], hasPrompt: message.role === "user" });
      continue;
    }
    rounds.at(-1)!.messages.push(message);
  }

  return rounds;
}

function promptOf(message: AccumulatedMessage): string {
  return message.parts
    .map((part) => (part.type === "text" ? part.text : ""))
    .filter((text) => text.trim())
    .join("\n")
    .trim();
}

function firstLine(text: string, maxLength = 160): string {
  const line = text.split("\n").find((candidate) => candidate.trim()) ?? "";
  const trimmed = line.trim();
  return trimmed.length > maxLength ? `${trimmed.slice(0, maxLength - 1)}…` : trimmed;
}

/** Newest first, matching the sidebar. */
export function deriveTurns(
  messages: readonly AccumulatedMessage[],
  delegations: readonly DelegationDto[] = [],
): SessionTurn[] {
  const rounds = toRounds(messages);
  const turns: SessionTurn[] = [];

  for (const [index, round] of rounds.entries()) {
    turns.push(toTurn(round, index + 1));
  }

  attachDelegations(turns, rounds, delegations);

  return turns.reverse();
}

function toTurn(round: Round, number: number): SessionTurn {
  const first = round.messages[0]!;
  const promptText = round.hasPrompt ? promptOf(first) : "";

  const models: string[] = [];
  let agent: string | undefined;
  let tokensInput = 0;
  let tokensOutput = 0;
  let cost = 0;
  let endedAt: number | undefined;

  const filesByPath = new Map<string, TurnFile>();
  const commands: TurnCommand[] = [];
  let toolCount = 0;
  let editCount = 0;

  for (const message of round.messages) {
    const finishedAt = message.completedAt ?? message.createdAt;
    if (finishedAt !== undefined && (endedAt === undefined || finishedAt > endedAt)) endedAt = finishedAt;

    if (message.role !== "assistant") continue;

    if (message.modelID && models.at(-1) !== message.modelID) models.push(message.modelID);
    agent ??= message.agent;
    cost += message.cost ?? 0;
    tokensInput += message.tokens?.input ?? 0;
    tokensOutput += message.tokens?.output ?? 0;

    for (const part of message.parts) {
      if (part.type !== "tool") continue;
      toolCount += 1;

      const tool = normalizeToolName(part.tool);

      if (SHELL_TOOLS.has(tool) && commands.length < 4) {
        const input = toolInput(part);
        const label = stringField(input, "command", "description");
        if (label) commands.push({ label: firstLine(label, 48), ok: commandOutcome(toolStatus(part)) });
      }

      const edits = fileEditsOf(part);
      if (edits.length === 0) continue;
      editCount += 1;

      for (const edit of edits) mergeFileEdit(filesByPath, edit);
    }
  }

  const files = [...filesByPath.values()];
  const additions = files.reduce((total, file) => total + file.additions, 0);
  const deletions = files.reduce((total, file) => total + file.deletions, 0);
  const startedAt = first.createdAt;
  const durationMs =
    startedAt !== undefined && endedAt !== undefined && endedAt > startedAt ? endedAt - startedAt : null;

  return {
    id: first.messageId,
    number,
    prompt: round.hasPrompt ? firstLine(promptText) : "",
    promptFull: promptText,
    hasPrompt: round.hasPrompt,
    modelId: models.at(-1),
    modelChanged: new Set(models).size > 1,
    agent,
    startedAt,
    endedAt,
    durationMs,
    tokensInput,
    tokensOutput,
    cost,
    files,
    additions,
    deletions,
    toolCount,
    editCount,
    commands,
    delegations: [],
    readOnly: files.length === 0,
  };
}

function commandOutcome(status: string | undefined): boolean | null {
  if (status === "completed") return true;
  if (status === "error") return false;
  return null;
}

function mergeFileEdit(files: Map<string, TurnFile>, edit: TurnFileEdit): void {
  const existing = files.get(edit.path);

  if (!existing) {
    const slash = edit.path.lastIndexOf("/");
    files.set(edit.path, {
      path: edit.path,
      name: slash >= 0 ? edit.path.slice(slash + 1) : edit.path,
      dir: slash >= 0 ? edit.path.slice(0, slash) : "",
      additions: edit.additions,
      deletions: edit.deletions,
      created: edit.created,
      diff: edit.diff,
    });
    return;
  }

  files.set(edit.path, {
    ...existing,
    additions: existing.additions + edit.additions,
    deletions: existing.deletions + edit.deletions,
    // A file this turn created and then edited again is still new this turn.
    created: existing.created || edit.created,
    diff: [...existing.diff, ...edit.diff],
  });
}

/**
 * A subagent belongs to the round that started it: by the tool call it came from, or failing that by
 * when it was created.
 */
function attachDelegations(turns: SessionTurn[], rounds: readonly Round[], delegations: readonly DelegationDto[]): void {
  if (delegations.length === 0) return;

  const roundByCallId = new Map<string, number>();
  rounds.forEach((round, index) => {
    for (const message of round.messages) {
      for (const part of message.parts) {
        if (part.type === "tool" && part.callId) roundByCallId.set(part.callId, index);
      }
    }
  });

  const collected: TurnDelegation[][] = turns.map(() => []);

  for (const delegation of delegations) {
    let index = delegation.parentToolCallId ? roundByCallId.get(delegation.parentToolCallId) : undefined;

    if (index === undefined) {
      const createdAt = delegation.createdAt ? Date.parse(delegation.createdAt) : Number.NaN;
      if (Number.isFinite(createdAt)) index = roundIndexAt(turns, createdAt);
    }

    if (index === undefined) continue;

    collected[index]!.push({
      id: delegation.delegationId,
      title: delegation.title,
      status: delegation.status,
      childSessionId: delegation.childSessionId,
    });
  }

  turns.forEach((turn, index) => {
    const found = collected[index]!;
    if (found.length > 0) turns[index] = { ...turn, delegations: found };
  });
}

/** The last round that had started by `at`. */
function roundIndexAt(turns: readonly SessionTurn[], at: number): number | undefined {
  let found: number | undefined;
  for (const [index, turn] of turns.entries()) {
    if (turn.startedAt !== undefined && turn.startedAt <= at) found = index;
  }
  return found;
}

// ─── Totals and display ─────────────────────────────────────────────────────

export interface TurnTotals {
  turns: number;
  additions: number;
  deletions: number;
}

export function turnTotals(turns: readonly SessionTurn[]): TurnTotals {
  return turns.reduce<TurnTotals>(
    (totals, turn) => ({
      turns: totals.turns + 1,
      additions: totals.additions + turn.additions,
      deletions: totals.deletions + turn.deletions,
    }),
    { turns: 0, additions: 0, deletions: 0 },
  );
}

/** A model the session can pick, as `useModels` reports it. */
export interface ModelNameOption {
  id: string;
  name: string;
}

/**
 * The friendly name for a model id, from the session's catalog. Kept in one function so the
 * attribution work happening in parallel can take it over without touching anything else.
 */
export function modelDisplayName(modelId: string | undefined, models: readonly ModelNameOption[]): string {
  if (!modelId) return "Unknown model";

  const exact = models.find((model) => model.id === modelId);
  if (exact) return exact.name;

  // Ids reach us both bare ("claude-opus-5") and qualified ("anthropic/claude-opus-5").
  const bare = modelId.slice(modelId.lastIndexOf("/") + 1);
  const loose = models.find((model) => model.id === bare || model.id.endsWith(`/${bare}`));
  if (loose) return loose.name;

  return bare;
}
