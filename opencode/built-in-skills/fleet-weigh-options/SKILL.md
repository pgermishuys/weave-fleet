---
name: fleet-weigh-options
description: Help the user choose how to build something. Reads the code, lays out two or three genuinely different approaches with their trade-offs in this codebase, and recommends one. No plan and no code. Use when the user knows what they want but not how, asks "which way should we do this", or is choosing between approaches or libraries.
---

# Weigh the options

The user knows what they want and is deciding how. Give them real choices and a recommendation they can act on. Not
a plan, not code.

## 1. Understand the goal

Make sure you know what they're trying to achieve and why, not only the feature they named. Read the code it
touches: the patterns already there, what calls it, what it calls, the constraints (platforms, performance, other
clients, what's tested).

Ask a question only when a missing fact would change the options, such as "Does this have to work offline?". If you
have a tool for asking the user questions with options, use it only for concrete choices like that.

## 2. Lay out two or three approaches

Real alternatives, not one idea in three sizes. Include the approach that fully does what they want even when it's
the most work.

For each:

- **What it is:** a sentence or two, naming the files or parts it touches
- **How well it does the job:** all of it, or part of it, and which part it leaves out
- **Fit:** how it matches, or fights, what the codebase already does
- **Cost:** effort, risk, how much it touches, what it's like to maintain in a year
- **Right when:** the situation where this is the one to pick

Keep each to a short paragraph or a few bullets. A table helps when the approaches differ on the same few points.

## 3. Recommend one

Say which you'd pick and why, anchored on what serves the user's goal, not on what's quickest to build. If the
approach that truly fits is also the hardest, recommend it and be plain about what it costs. Prefer a simpler one
only when it meets the goal about as well.

Name what would change your mind: "A, unless you expect more than one provider, then B."

## 4. Stop there

Don't start building and don't write a step-by-step plan. Once they pick, they can plan it (fleet-plan-feature) or
ask you to build it.
