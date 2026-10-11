import type { Register } from "fleet-mods";

// The agent's `git push` is refused while the session is on main; the agent is told why and can push a branch instead.
// Needs the session to ask for shell commands (Settings → Permissions): an ask never made is never seen.
export const register: Register = (on) => {
  on("permission.ask", { kind: "shell", title: /\bgit\s+push\b/ }, async ($, e, next) => {
    if ((await $.session.branch()) === "main") return { decision: "deny", reason: "Don't push from main. Make a branch and push that instead." };
    return next(e);
  });
};
