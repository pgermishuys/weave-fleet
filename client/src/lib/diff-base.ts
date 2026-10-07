import type { SessionDiffBase } from "@/api/client";

export interface DiffBaseLabel {
  /** "Compared with main", or "Since this session started". */
  text: string;
  /** The short commit the branch left main at; null when compared with the session start. */
  commit: string | null;
  /** What that means, for a tooltip. */
  detail: string;
}

/** How the Changes list says what the session's changes are compared with. */
export function diffBaseLabel(base: SessionDiffBase | null | undefined): DiffBaseLabel | null {
  if (!base) return null;
  if (base.kind === "branch") {
    const branch = base.branch || "main";
    const commit = base.commit ? base.commit.slice(0, 7) : null;
    return {
      text: `Compared with ${branch}`,
      commit,
      detail: `What this branch changes since it left ${branch}${commit ? ` at ${commit}` : ""}, committed or not. Rebasing onto ${branch} or pulling it doesn't add other people's work.`,
    };
  }
  return {
    text: "Since this session started",
    commit: null,
    detail: "This folder isn't on a branch of its own, so its changes are compared with how it was when the session started.",
  };
}
