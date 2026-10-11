import type { Register } from "fleet-mods";

// When a prompt mentions branches, PRs, pushing or merging, the model is told the session's current git branch. The
// note is model-only: the conversation shows what you typed.
export const register: Register = (on) => {
  on("prompt.submit", { text: /branch|\bprs?\b|pull request|push|merge/i }, async ($, e, next) => {
    const branch = await $.session.branch();
    if (!branch) return next(e);
    return next({ ...e, context: [...e.context, `Fleet read the session's git branch just now (git symbolic-ref HEAD in the session folder): ${branch}. It is current; answer from it.`] });
  });
};
