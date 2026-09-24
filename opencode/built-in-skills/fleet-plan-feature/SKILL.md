---
name: fleet-plan-feature
description: Plan a feature with the user, back and forth, before any code is written. Reads the code first, asks a few grounded questions at a time until every decision is made, then writes an implementation plan. Use when the user wants to plan, scope or think through a feature, or says "let's plan".
---

# Plan a feature together

The user has an idea, often a sentence. Turn it into a plan they have agreed to, one decision at a time. Don't
guess: every choice in the plan is either in the code or the user's answer.

This is a conversation. Don't write code, and don't produce the plan in your first reply.

## 1. Read before you ask

If the user hasn't said what they want, ask in one plain sentence and wait.

Once they have, read the code it touches: where it would live, the patterns next to it, the data it reads and writes,
the tests around it, `AGENTS.md` or `CLAUDE.md`. A question the code can answer is a question you shouldn't ask.

## 2. Ask a few at a time

Ask at most three questions per reply, numbered, so they can answer in one message. Make them decisions, not
homework:

- "Should archived sessions count toward the limit? (a) yes, (b) no, they're hidden anyway"
- not "How should archiving work?"

Say what you found that makes it a question: "`SessionList` already filters by status, so (b) is one line."

If you have a tool for asking the user questions with options, use it only when the options are concrete and come
from the code or the conversation. Ask open questions, such as what they want in the first place, in plain text.
Never invent options to fill the tool.

## 3. Raise what they haven't thought of

With each round, bring up what the code shows and the idea doesn't mention: edge cases, other callers, migrations,
older clients, other platforms, what happens on failure, what it costs. Put each one to them as a question. Don't
decide it quietly.

## 4. Keep going until nothing is open

After each answer, read whatever the answer makes relevant, then ask the next round. Stop when there's nothing left to
decide, not when it feels long. If the user says "you decide", decide, and say what you chose.

## 5. Write the plan

When everything is settled, write it to `.weave/plans/<short-name>.md` unless the user wants it elsewhere or only in
the chat:

```markdown
# <Feature>

<Two or three sentences: what it does and why.>

## Decisions
- <each decision, with the user's reason when they gave one>

## Steps
1. <file or area>: <the change>. Test: <the test that proves it>.

## Risks
- <what could go wrong, and how you'd notice>

## Assumptions
- <anything still assumed, or "None">
```

Order the steps so each one builds and passes its tests on its own. Name real files. Keep it short enough to read
in two minutes.

## 6. Offer the next step

Show the plan and ask whether to build it. Build it only when they say so. If they'd like it built somewhere else,
the fleet-api skill can start a new Fleet session for it (in a worktree, so this folder stays as it is). That session
can't see this conversation, so give it the plan's path, or the whole plan.
