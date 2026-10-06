# Claude Code stream fixtures

Trimmed copies of real `claude -p --output-format stream-json --verbose` output (Claude Code 2.1.273).
Each line keeps Claude Code's own key order: result lines and tool results don't start with `"type"`.
Bulky fields (tool lists, model usage, thinking signatures) were dropped and the working directory
replaced with `<WORKDIR>`.

- `two-tools.jsonl`: a prompt that reads a file, runs a Bash command, then answers.
- `max-turns.jsonl`: the same kind of prompt run with `--max-turns 1`; ends in an `error_max_turns` result.
- `not-logged-in.jsonl`: a run that can't use the login (`--bare` with a claude.ai subscription);
  Claude Code writes "Not logged in · Please run /login" as the assistant's reply.
- `background-wake.jsonl`: one process with stream-json input (Claude Code 2.1.289). The first prompt starts
  `sleep 20 && echo bgdone` with `run_in_background` and the turn ends; when the command finishes, Claude Code reports
  it (`background_tasks_changed`, `task_notification`) and starts a turn by itself with a new `init`; then a second
  prompt from the host. Thinking-only assistant lines and `thinking_tokens` lines were dropped too, and the task
  output folder replaced with `<TASKS>`.
- `background-shell-and-monitor.jsonl`, `subagents.jsonl`, `stop-task.jsonl` (Claude Code 2.1.289, 2026-10-04): a
  background `Bash` command and a `Monitor`; a foreground subagent starting a nested one plus a background subagent
  with a background shell of its own; a background command stopped with `stop_task` while the turn ran a foreground
  one. Thinking, usage and `task_updated` lines dropped, the task folder replaced with `<TASKS>`. The running work
  each gives is in `tests/contracts/claudecode-work-events.json`.
- `partial-messages.jsonl`, `file-tools.jsonl`, `ask-user-question.jsonl`, `initialize.json` (Claude Code 2.1.290,
  2026-10-06, `--model haiku`): a reply streamed with `--include-partial-messages` (`stream_event` lines; signature deltas
  dropped); Read, Edit (three times; this build has no MultiEdit, Glob, Grep or TodoWrite), Write, Bash and TaskCreate
  calls, with each tool result's `tool_use_result` (an edit's `structuredPatch`, a new file's `type: create`), minus
  `originalFile`; an `AskUserQuestion` call asked as `can_use_tool` (`--permission-mode default --permission-prompt-tool
  stdio`) and answered with `updatedInput: { questions, answers }`; and the answer to an `initialize` control request, its
  model list cut to a few and everything but `models` dropped. The working directory is `/work`.
