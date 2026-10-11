import type { Register } from "fleet-mods";

// Draws `git status` (long, --short or --porcelain) as staged, modified and untracked counts, and `git log --oneline`
// as a commit count.
export type Status = { branch?: string; staged: number; modified: number; untracked: number };

const ANSI = new RegExp(String.fromCharCode(27) + "\\[[0-9;]*m", "g");

/** Reads `git status` output in either format. Undefined when it isn't one. */
export function parseStatus(raw: string): Status | undefined {
  const lines = raw.replace(ANSI, "").split(/\r?\n/);
  const status: Status = { staged: 0, modified: 0, untracked: 0 };

  const long = lines.find((l) => /^On branch |^HEAD detached /.test(l));
  if (long) {
    status.branch = long.replace(/^On branch /, "").replace(/^HEAD detached (at|from) /, "");
    let section = "";
    for (const line of lines) {
      if (/^Changes to be committed:/.test(line)) section = "staged";
      else if (/^Changes not staged for commit:/.test(line)) section = "modified";
      else if (/^Untracked files:/.test(line)) section = "untracked";
      else if (/^Unmerged paths:/.test(line)) section = "modified";
      else if (/^\t\S/.test(line) && section) status[section as "staged" | "modified" | "untracked"] += 1;
    }
    return status;
  }

  // --short / --porcelain: "XY path", with an optional "## branch...origin/branch" first line (-b).
  const entries = lines.filter((l) => /^[ MADRCU?!]{2} \S/.test(l));
  const head = lines.find((l) => l.startsWith("## "));
  if (head) status.branch = head.slice(3).split("...")[0];
  if (entries.length === 0 && !head) return undefined;
  for (const l of entries) {
    if (l.startsWith("??")) status.untracked += 1;
    else {
      if (l[0] !== " ") status.staged += 1;
      if (l[1] !== " ") status.modified += 1;
    }
  }
  return status;
}

/** Counts the commits `git log` printed: one per line with --oneline, one "commit <sha>" header otherwise. */
export function countCommits(raw: string): number | undefined {
  const lines = raw.replace(ANSI, "").split(/\r?\n/);
  const oneline = lines.filter((l) => /^[0-9a-f]{7,40}( |$)/.test(l)).length;
  const full = lines.filter((l) => /^commit [0-9a-f]{40}/.test(l)).length;
  return oneline + full > 0 ? oneline + full : undefined;
}

export const register: Register = (on) => {
  on("ui.render", { component: "ToolUse", props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolUse") return next(e);
    if (e.props.status !== "completed" && e.props.status !== "error") return next(e); // not while it runs
    const command = String(e.props.input.command ?? "");
    const { Box, Text, Pill, Icon } = $.ui.resolve(e);

    if (/\bgit (-C \S+ )?status\b/.test(command)) {
      const s = parseStatus(e.props.output);
      if (!s) return next(e);
      const clean = s.staged + s.modified + s.untracked === 0;
      return Box({
        flexDirection: "row",
        gap: 1,
        alignItems: "center",
        children: [
          s.branch && Icon({ name: "git-branch", color: "muted" }),
          s.branch && Text({ code: true, dimColor: true, children: [s.branch] }),
          clean && Pill({ tone: "good", label: "clean" }),
          s.staged > 0 && Pill({ tone: "good", label: `${s.staged} staged` }),
          s.modified > 0 && Pill({ tone: "warn", label: `${s.modified} modified` }),
          s.untracked > 0 && Pill({ tone: "neutral", label: `${s.untracked} untracked` }),
        ],
      });
    }

    if (/\bgit (-C \S+ )?log\b/.test(command)) {
      const commits = countCommits(e.props.output);
      if (commits === undefined) return next(e);
      return Pill({ tone: "accent", label: `${commits} commit${commits === 1 ? "" : "s"}` });
    }
    return next(e);
  });
};
