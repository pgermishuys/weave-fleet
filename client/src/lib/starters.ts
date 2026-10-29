/** Settings → General → Show starters, and the row's own "Hide starters". On unless turned off. */
export const STARTERS_PREFERENCE_KEY = "NewSessionStarters";

/** The built-in Fix a bug workflow, which the workflow starter opens. */
export const FIX_A_BUG_WORKFLOW_ID = "builtin:fix-a-bug";

export interface TextStarter {
  id: "explore" | "plan" | "review";
  label: string;
  /** What goes in the box. A "…" marks the part the person fills in. */
  text: string;
}

/** Fleet's own starters: plain text, so they mean the same to every harness. */
export const BUILT_IN_STARTERS: readonly TextStarter[] = [
  { id: "explore", label: "Explore the code", text: "Explore this repository and explain how … works. Don't change any files." },
  { id: "plan", label: "Plan a change", text: "I want to … Read the code and propose a plan. Don't change any files yet." },
  { id: "review", label: "Review my changes", text: "Review my uncommitted changes. List bugs and risks, most serious first. Don't edit anything." },
];

/** What to select once a starter fills the box: its "…", so typing replaces it, or nothing at the end. */
export function starterSelection(text: string): [start: number, end: number] {
  const at = text.indexOf("…");
  return at >= 0 ? [at, at + 1] : [text.length, text.length];
}
