/**
 * A session's drafts and the Mods switch, as `/api/sessions/{id}/mods` and `/api/features/mods` answer them
 * (`ModEndpoints.cs`, `docs/mods/api.md` "Drafts, Keep and Undo").
 */

/** Why a mod or a draft is off: the user turned it off, or three failures in a row did (with the last one). */
export interface ModOff {
  by: "user" | "strikes";
  at: string;
  error?: string | null;
}

export interface ModCheckProblem {
  line?: number;
  column?: number;
  code: string;
  message: string;
}

export interface ModCheckHook {
  event: string;
  /** The matcher as JSON; a RegExp as `{ "$regex": source, "flags": flags }`. */
  matcher?: unknown;
}

/** What the static check read from a mod without running it (`CheckReport` in `mods/types/fleet-mods.d.ts`). */
export interface ModCheckReport {
  ok: boolean;
  name: string;
  version: string;
  description: string;
  lines: number;
  sha256: string;
  hooks: ModCheckHook[];
  /** Every `$` call as `ns.method`, sorted, once each. */
  calls: string[];
  state: string[];
  pages: string[];
  /** The panes `mod.json` declares, listed in the session's "+" menu. */
  panes?: Array<{ id: string; title: string; icon?: string }>;
  errors: ModCheckProblem[];
  warnings: ModCheckProblem[];
}

/** A mod the agent is writing in one session. */
export interface ModDraft {
  sessionId: string;
  name: string;
  /** Null while its `mod.json` is missing or invalid (the agent is still writing it). */
  description: string | null;
  version: string | null;
  off: ModOff | null;
  /** The active version of the kept mod of the same name, which the draft stands in for in its session. */
  kept: number | null;
  /** The agent asked the user to keep it (`fleet_mod_keep`), with its note; cleared by Keep, Discard or Turn off. */
  keepRequest?: ModKeepRequest | null;
  /** Why the draft's last load was refused (from the mod host); null or absent when it loaded or hasn't been tried. */
  problem?: ModDraftProblem | null;
}

export interface ModDraftProblem {
  code: string;
  message: string;
  line?: number | null;
}

export interface ModKeepRequest {
  note: string | null;
  at: string;
}

/** `GET /api/features/mods`: the Mods switch as the server sees it, and "Start without mods". */
export interface ModsSwitch {
  on: boolean;
  safeMode: boolean;
}

/** One kept version. Fleet numbers them `v1`, `v2`…; `version` is the author's own string. */
export interface ModVersion {
  number: number;
  createdAt: string;
  version: string;
  sha256: string;
  sessionId: string | null;
  sessionTitle: string | null;
  note: string | null;
}

export interface KeptMod {
  name: string;
  description: string | null;
  /** The version sessions load; null only before anything is kept. */
  active: number | null;
  /** The active version's own version string. */
  activeVersion: string | null;
  off: ModOff | null;
  /** Oldest first. */
  versions: ModVersion[];
}

export interface ModsView {
  safeMode: boolean;
  mods: KeptMod[];
}

/** `?mods=off` on any Fleet address starts without mods. */
export const MODS_OFF_QUERY = "mods";
export const MODS_OFF_VALUE = "off";
