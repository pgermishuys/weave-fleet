# Claude Code stream fixtures

Trimmed copies of real `claude -p --output-format stream-json --verbose` output (Claude Code 2.1.273).
Each line keeps Claude Code's own key order: result lines and tool results don't start with `"type"`.
Bulky fields (tool lists, model usage, thinking signatures) were dropped and the working directory
replaced with `<WORKDIR>`.

- `two-tools.jsonl`: a prompt that reads a file, runs a Bash command, then answers.
- `max-turns.jsonl`: the same kind of prompt run with `--max-turns 1`; ends in an `error_max_turns` result.
- `not-logged-in.jsonl`: a run that can't use the login (`--bare` with a claude.ai subscription);
  Claude Code writes "Not logged in · Please run /login" as the assistant's reply.
