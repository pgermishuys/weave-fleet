/**
 * `$` skills: `$fleet-debug` anywhere in a message names one of the session's skills, picked from the composer's `$`
 * list or typed in full. The token stays in the text as typed, so a message keeps its skills after a reload from the
 * harness's store, and the conversation draws each one as a chip. The skills go to the server as a list beside the
 * text, and the harness decides how each one reaches the agent (see `skillDeliveries`). A `$` that names no skill
 * (`$HOME`, `$5`) is plain text, and so is one inside backticks.
 */
import { reactive } from "vue";
import type { AutocompleteCommand } from "@/api/client";

export interface SessionSkill {
  name: string;
  description?: string;
  /** The model can't load it (Claude Code's `disable-model-invocation`): it only runs from the front of a message. */
  userOnly?: boolean;
}

/** How a skill reaches the agent: with the message, or as a note naming it that the agent loads it from. */
export type SkillDelivery = "attached" | "note";

export interface SkillMention {
  name: string;
  delivery: SkillDelivery;
}

// "$" at the start or after whitespace, then everything up to the next whitespace (as `@` references are found).
const SKILL_TOKEN = /(^|\s)(\$[^\s$][^\s]*)/g;
// Sentence punctuation typed straight after a token isn't part of it.
const TRAILING_PUNCTUATION = /[.,;:!?)\]}'"]+$/;

/** Whether `index` is inside backticks: code, where a `$` is the code's own. */
export function insideBackticks(text: string, index: number): boolean {
  let ticks = 0;
  for (let at = text.indexOf("`"); at !== -1 && at < index; at = text.indexOf("`", at + 1)) ticks += 1;
  return ticks % 2 === 1;
}

/** Each `$name` token in `text` that names one of `skills`: where it starts and ends, `$` included. */
export function skillTokens(text: string, skills: ReadonlySet<string>): { name: string; start: number; end: number }[] {
  const tokens: { name: string; start: number; end: number }[] = [];
  if (skills.size === 0 || !text.includes("$")) return tokens;
  for (const match of text.matchAll(SKILL_TOKEN)) {
    const token = match[2].replace(TRAILING_PUNCTUATION, "");
    const start = (match.index ?? 0) + match[1].length;
    const name = token.slice(1);
    if (skills.has(name) && !insideBackticks(text, start)) tokens.push({ name, start, end: start + token.length });
  }
  return tokens;
}

/** The skills `text` names, each once, in the order they first appear. What goes to the server with the message. */
export function skillNamesIn(text: string, skills: ReadonlySet<string>): string[] {
  return [...new Set(skillTokens(text, skills).map((token) => token.name))];
}

/**
 * How each skill reaches the agent on a harness. OpenCode 2 takes skills with the message. Claude Code expands only
 * the skill a message starts with, so Fleet moves the first one there and names the rest in a note. Every other
 * harness gets the note.
 */
export function skillDeliveries(harnessType: string | null | undefined, names: readonly string[]): SkillMention[] {
  return names.map((name, index) => ({
    name,
    delivery: harnessType === "opencode2" || (harnessType === "claude-code" && index === 0) ? "attached" : "note",
  }));
}

/** What the `$` list says about a skill picked on this harness, for its footer. */
export function skillListHint(harnessType: string | null | undefined, harnessName: string): string {
  if (harnessType === "opencode2") return `${harnessName} loads it with the message`;
  if (harnessType === "claude-code") return `${harnessName} runs the first skill in a message`;
  return `${harnessName} gets a note naming it, then loads it`;
}

/** Where a `$` the caret is typing starts, and what's typed after it; null when the caret isn't in one. */
export function skillTrigger(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret);
  const start = before.lastIndexOf("$");
  if (start === -1 || (start > 0 && !/\s/.test(before[start - 1]))) return null;
  const query = before.slice(start + 1);
  if (/\s|\$/.test(query) || insideBackticks(text, start)) return null;
  return { start, query };
}

// ── The skills each session offers ────────────────────────────────────────

/** Each session's skills, as its `/` list loaded them: the composer's `$` list, and the chips in its messages. */
const known = reactive<Record<string, SessionSkill[]>>({});

/** Keeps the skills among a session's commands (a command its harness says isn't a skill is left out). */
export function rememberSessionSkills(sessionId: string, commands: readonly AutocompleteCommand[]): void {
  known[sessionId] = commands
    .filter((command) => command.kind !== "command")
    .map(({ name, description, userOnly }) => ({ name, description, ...(userOnly ? { userOnly } : {}) }));
}

/** The session's skills (reactive); none until its commands have loaded. */
export function sessionSkills(sessionId: string | null | undefined): readonly SessionSkill[] {
  return (sessionId && known[sessionId]) || [];
}

/** The names of the session's skills (reactive). */
export function sessionSkillNames(sessionId: string | null | undefined): ReadonlySet<string> {
  return new Set(sessionSkills(sessionId).map((skill) => skill.name));
}

/** For tests: forgets every session's skills. */
export function resetSessionSkills(): void {
  for (const key of Object.keys(known)) delete known[key];
}

// ── Chips in a rendered message ──────────────────────────────────────────

// lucide's scroll-text, so the chip needs no component inside rendered Markdown.
const CHIP_ICON = '<svg class="skill-ref-chip__icon" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
  + 'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
  + '<path d="M15 12h-5"/><path d="M15 8h-5"/><path d="M19 17V5a2 2 0 0 0-2-2H4"/>'
  + '<path d="M8 21h12a2 2 0 0 0 2-2v-1a1 1 0 0 0-1-1H11a1 1 0 0 0-1 1v1a2 2 0 1 1-4 0V5a2 2 0 1 0-4 0v2a1 1 0 0 0 1 1h3"/></svg>';

const ESCAPE = /[.*+?^${}()|[\]\\]/g;

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (character) => `&${({ "&": "amp", "<": "lt", ">": "gt", "\"": "quot", "'": "#39" } as const)[character as "&"]};`);
}

function chip(mention: SkillMention): string {
  const name = escapeHtml(mention.name);
  const note = mention.delivery === "note";
  return `<span class="skill-ref-chip${note ? " skill-ref-chip--note" : ""}" data-skill-ref="${name}" `
    + `title="${note ? `Skill ${name}, sent as a note for the agent to load` : `Skill ${name}`}">`
    + `${CHIP_ICON}<span class="skill-ref-chip__name">${name}</span></span>`;
}

/** Rendered Markdown with each `$name` drawn as a chip. Tokens inside code are left as they are. */
export function withSkillChips(html: string, mentions: readonly SkillMention[]): string {
  if (mentions.length === 0) return html;

  let inCode = 0;
  return html
    .split(/(<[^>]+>)/)
    .map((piece) => {
      if (piece.startsWith("<")) {
        if (/^<(code|pre)\b/i.test(piece)) inCode += 1;
        else if (/^<\/(code|pre)>/i.test(piece)) inCode = Math.max(0, inCode - 1);
        return piece;
      }
      if (inCode > 0 || !piece.includes("$")) return piece;
      return mentions.reduce(
        (text, mention) => text.replace(
          new RegExp(`(^|\\s)\\$${mention.name.replace(ESCAPE, "\\$&")}(?![^\\s.,;:!?)\\]}'"&]|[.,;:!?][^\\s.,;:!?)\\]}'"&])`, "g"),
          (_match, before: string) => `${before}${chip(mention)}`,
        ),
        piece,
      );
    })
    .join("");
}
