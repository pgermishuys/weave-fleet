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
