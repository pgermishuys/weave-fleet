/**
 * A finished turn folds into one line: "Worked for 34s · read 4 files · edited 2 · ran 2 commands", with the answer and a
 * changed-files card after it. This reads the conversation's message views (so it's the same live and after a reload,
 * and for every harness: the tool registry's categories decide what a call is, not one harness's shapes).
 *
 * A turn is one message of yours and every agent message until the next one. It folds when it isn't the turn still
 * running. Folding hides the turn's thinking and its completed tool rows; failed, stopped and background rows, sub-agent
 * rows, pages and screenshots stay (they're part of the answer, or a failure is never folded away).
 */
import type { ActivityMessage } from "@/composables/use-activity-messages";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";
import { isReadCall, isShellRow, isWriteCall } from "@/lib/tool-row";

export interface FoldedFile {
  /** The path as the session's folder knows it (relative when the call named an absolute path inside it). */
  path: string;
  name: string;
  dir: string;
  additions: number;
  deletions: number;
  /** The file was written whole and its real change isn't known yet: the counts are 0 and shouldn't be drawn. */
  unknownCounts?: boolean;
}

export interface TurnSummary {
  durationMs?: number;
  reads: number;
  edits: number;
  commands: number;
  other: number;
  /** Calls left visible because they failed or didn't finish. */
  failed: number;
}

export interface FoldedTurn {
  /** The id of the turn's first message: what the open state is kept by. */
  id: string;
  summary: TurnSummary;
  files: FoldedFile[];
  additions: number;
  deletions: number;
  /** False when no file's counts are known (every one was written whole and the session's diff isn't loaded). */
  countsKnown: boolean;
}

/** What a message view gets from folding. */
export interface FoldMark {
  /** The turn's fold row draws above this message's content. */
  fold?: FoldedTurn;
  /** The turn's changed-files card draws below this message's content. */
  card?: FoldedTurn;
  /** The message has nothing left but the fold row: its own content was all folded away. */
  foldOnly?: boolean;
}

export type FoldedMessage = ActivityMessage & FoldMark;

/** A call the fold keeps on screen. */
function staysVisible(tool: ToolCardItem): boolean {
  return Boolean(tool.delegation || tool.page || tool.screenshot) || tool.status !== "Completed";
}

const NO_REASONING: never[] = [];
/** The calls a folded message keeps, kept per call list so an unchanged message hands its bubble the same array. */
const visibleCache = new WeakMap<readonly ToolCardItem[], ToolCardItem[]>();
function visibleTools(tools: readonly ToolCardItem[]): ToolCardItem[] {
  let kept = visibleCache.get(tools);
  if (!kept) {
    kept = tools.filter(staysVisible);
    visibleCache.set(tools, kept);
  }
  return kept;
}

function relativePath(path: string, directory: string | undefined): string {
  if (!directory) return path;
  const base = directory.replace(/[\\/]+$/, "");
  return path.startsWith(`${base}/`) || path.startsWith(`${base}\\`) ? path.slice(base.length + 1) : path;
}

/**
 * A call that wrote the whole file has no change of its own to count: its lines are all additions. The change is the
 * file's session diff (see {@link withFileStats}); until that's known the call has no counts.
 */
function countDiff(tool: ToolCardItem): { additions: number; deletions: number; unknown?: boolean } {
  if (tool.diffFromContent) {
    return tool.diffStat ? { ...tool.diffStat } : { additions: 0, deletions: 0, unknown: true };
  }
  let additions = 0;
  let deletions = 0;
  for (const line of tool.diffLines ?? []) {
    if (line.type === "add") additions += 1;
    else if (line.type === "remove") deletions += 1;
  }
  return { additions, deletions };
}

function hasContent(message: ActivityMessage): boolean {
  return message.body.trim().length > 0
    || message.images.length > 0
    || (message.tools?.length ?? 0) > 0
    || (message.questionParts?.length ?? 0) > 0
    || message.turnError != null
    || message.background != null
    || message.compaction != null
    || message.shell != null;
}

interface TurnRange {
  user: ActivityMessage | null;
  /** Indices of the turn's agent messages. */
  agent: number[];
}

function turnRanges(messages: readonly ActivityMessage[]): TurnRange[] {
  const ranges: TurnRange[] = [];
  let current: TurnRange | null = null;
  messages.forEach((message, index) => {
    if (message.role === "user") {
      current = { user: message, agent: [] };
      ranges.push(current);
    } else if (message.role === "assistant") {
      if (!current) {
        current = { user: null, agent: [] };
        ranges.push(current);
      }
      current.agent.push(index);
    } else {
      // A shell block of yours ends the turn it sits in.
      current = null;
    }
  });
  return ranges;
}

/** The file's change as the session's diff counts it, by path as the session's folder knows it. */
export type FileStats = ReadonlyMap<string, { additions: number; deletions: number }>;

const statted = new WeakMap<readonly ToolCardItem[], { stats: FileStats | null; tools: ToolCardItem[] }>();

/**
 * Gives every call that wrote a whole file the file's real change from the session's diff (the same numbers as the
 * Changes panel), or null while that isn't known. A file the diff doesn't list has no net change. Messages that carry
 * no such call are returned as they are.
 */
export function withFileStats(
  messages: readonly ActivityMessage[],
  stats: FileStats | null,
  directory: string | undefined,
): ActivityMessage[] {
  return messages.map((message) => {
    const tools = message.tools;
    if (!tools?.some((tool) => tool.diffFromContent)) return message;
    const cached = statted.get(tools);
    if (cached && cached.stats === stats) return { ...message, tools: cached.tools };
    const next = tools.map((tool) => {
      if (!tool.diffFromContent || !tool.filePath) return tool;
      const found = stats ? (stats.get(relativePath(tool.filePath, directory)) ?? { additions: 0, deletions: 0 }) : null;
      return { ...tool, diffStat: found };
    });
    statted.set(tools, { stats, tools: next });
    return { ...message, tools: next };
  });
}

export interface FoldOptions {
  /** The session is working: its last turn is still running and stays open. */
  running: boolean;
  /** The session's folder, so a call that named an absolute path shows it as the folder knows it. */
  directory?: string;
  /** Turn ids the user opened: their rows show again (the fold row and the card stay). */
  open: ReadonlySet<string>;
}

/**
 * The messages as they draw once finished turns fold. A turn with nothing to fold (no calls, no thinking) and no
 * changed files is left as it is.
 */
export function foldTurns(messages: readonly ActivityMessage[], options: FoldOptions): FoldedMessage[] {
  const out: (FoldedMessage | null)[] = [...messages];
  const ranges = turnRanges(messages);

  ranges.forEach((range, rangeIndex) => {
    if (range.agent.length === 0) return;
    const isLast = rangeIndex === ranges.length - 1;
    if (isLast && options.running) return;

    const agentMessages = range.agent.map((index) => messages[index]!);
    const turnId = (range.user ?? agentMessages[0]!).id;

    const summary: TurnSummary = { reads: 0, edits: 0, commands: 0, other: 0, failed: 0 };
    const files = new Map<string, FoldedFile>();
    let hasThinking = false;
    let foldable = 0;
    let endedAt: number | undefined;

    for (const message of agentMessages) {
      const finished = message.completedAt ?? message.createdAt;
      if (finished !== undefined && (endedAt === undefined || finished > endedAt)) endedAt = finished;
      if (message.reasoningParts?.some((part) => part.text.trim() || part.summary?.trim())) hasThinking = true;

      for (const tool of message.tools ?? []) {
        if (tool.delegation) continue;
        if (tool.status !== "Completed") {
          summary.failed += 1;
          continue;
        }
        if (isReadCall(tool.kind)) summary.reads += 1;
        else if (isShellRow(tool.kind)) summary.commands += 1;
        else if (isWriteCall(tool.kind)) {
          const filePath = tool.filePath;
          const { additions, deletions, unknown } = countDiff(tool);
          if (filePath) {
            const path = relativePath(filePath, options.directory);
            const existing = files.get(path);
            if (existing) {
              // A whole-file write's stat is the file's whole session change: the latest one stands, it isn't summed.
              if (tool.diffFromContent && !unknown) {
                existing.additions = additions;
                existing.deletions = deletions;
                existing.unknownCounts = undefined;
              } else if (!existing.unknownCounts) {
                existing.additions += additions;
                existing.deletions += deletions;
              }
            } else {
              const slash = path.lastIndexOf("/");
              files.set(path, {
                path,
                name: slash >= 0 ? path.slice(slash + 1) : path,
                dir: slash >= 0 ? path.slice(0, slash) : "",
                additions,
                deletions,
                ...(unknown ? { unknownCounts: true } : {}),
              });
            }
          }
        } else summary.other += 1;
        if (!staysVisible(tool)) foldable += 1;
      }
    }
    summary.edits = files.size;
    const started = range.user?.createdAt;
    if (started !== undefined && endedAt !== undefined && endedAt > started) summary.durationMs = endedAt - started;

    const fileList = [...files.values()];
    const turn: FoldedTurn = {
      id: turnId,
      summary,
      files: fileList,
      additions: fileList.reduce((total, file) => total + file.additions, 0),
      deletions: fileList.reduce((total, file) => total + file.deletions, 0),
      countsKnown: fileList.some((file) => !file.unknownCounts),
    };

    const folds = (foldable > 0 || hasThinking);
    // Text the agent wrote before or between its calls is narration of the work: it folds with it. Only the text after
    // the last call is the answer. A turn whose text all came before its last call keeps the last of it as the answer.
    let lastWork = -1;
    range.agent.forEach((messageIndex, position) => {
      const message = messages[messageIndex]!;
      if ((message.tools?.some((tool) => !tool.delegation) ?? false) || message.reasoningParts?.some((part) => part.text.trim() || part.summary?.trim())) lastWork = position;
    });
    let answerFrom = lastWork + 1;
    if (!agentMessages.slice(answerFrom).some((message) => message.body.trim())) {
      for (let position = agentMessages.length - 1; position >= 0; position -= 1) {
        if (agentMessages[position]!.body.trim()) {
          answerFrom = position;
          break;
        }
      }
    }
    const collapsed = folds && !options.open.has(turnId);
    let last: number | null = null;

    range.agent.forEach((messageIndex, position) => {
      const message = messages[messageIndex]!;
      let view: FoldedMessage = message;
      if (collapsed) {
        view = {
          ...message,
          tools: message.tools ? visibleTools(message.tools) : message.tools,
          reasoningParts: NO_REASONING,
          body: position < answerFrom ? "" : message.body,
        };
      }
      const isHost = position === 0;
      if (collapsed && !hasContent(view)) {
        // Nothing left of this message. The first one stays as the fold row's host; the rest go.
        if (isHost) {
          view = { ...view, foldOnly: true };
        } else {
          out[messageIndex] = null;
          return;
        }
      }
      if (isHost && folds) view = { ...view, fold: turn };
      out[messageIndex] = view;
      last = messageIndex;
    });

    if (fileList.length > 0 && last !== null) {
      out[last] = { ...(out[last] as FoldedMessage), card: turn };
    }
  });

  return out.filter((message): message is FoldedMessage => message !== null);
}

const plural = (n: number, one: string, many = `${one}s`): string => `${n} ${n === 1 ? one : many}`;

/** "read 4 files · edited 2 · ran 2 commands": what the turn did, in the order of the fold line. */
export function summaryParts(summary: TurnSummary): string[] {
  const parts: string[] = [];
  if (summary.reads > 0) parts.push(`read ${plural(summary.reads, "file")}`);
  if (summary.edits > 0) parts.push(`edited ${summary.edits}`);
  if (summary.commands > 0) parts.push(`ran ${plural(summary.commands, "command")}`);
  if (summary.other > 0) parts.push(`${plural(summary.other, "other call")}`);
  if (summary.failed > 0) parts.push(`${summary.failed} failed`);
  return parts;
}
