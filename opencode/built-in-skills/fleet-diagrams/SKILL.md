---
name: fleet-diagrams
description: Draw a diagram as an HTML page and show it beside the chat, in Fleet's colours and type. Architecture, flows and workflows, sequences, data flow, state machines, database tables, class contracts, trees, timelines. Use it for every diagram, instead of the fleet_canvas_open canvas: when the user asks for a diagram or a picture of how something is built or flows, or asks for fleet-diagrams by name. Not for how something works over time as an animation (fleet-explain), UI mockups (fleet-mockups) or numeric charts.
---

# Draw a diagram

You write a small JSON file that describes the diagram. archify (MIT, by tt-a1i) checks it and renders it as one HTML
file. Fleet shows that file in a tab and puts its own theme on it, so you never style anything.

```
<this skill's folder>/archify/
  ARCHIFY.md                 ← archify's own instructions (its SKILL.md, renamed so it isn't a second skill)
  bin/archify.mjs            ← the command
  schemas/  examples/        ← one schema and one example per diagram type
  references/                ← only when a step below says so
```

If there is no `archify/` folder next to this file (the user keeps their own version of the skill), find
`fleet-diagrams/archify/bin/archify.mjs` under Fleet's data folder and use the folder it is in.

## 1. Check the runtime

Run `node --version`. Node 18 or newer is enough. If there is no `node`, run `bun --version` and use `bun` wherever
this page says `node`. If there is neither, tell the user plainly: "Diagrams need Node 18+ or Bun on your PATH, and
Fleet doesn't install them." Then stop. Don't install anything and don't draw the diagram another way.

## 2. Read, once

Read these and nothing more (archify's own text is long and mostly for other cases):

- `archify/ARCHIFY.md`, the lines from `## Fast authoring path` to the end of `## Type router`, and `## Delivery`.
  Skip the update, Mermaid, viewer and setup sections. archify's paths in it are relative to `archify/`, and
  `.archify/` folders are replaced by step 3 below.
- `archify/references/authoring-defaults.md`.
- The example for your type (the Type router names it), and for every type except `architecture` its schema in
  `archify/schemas/` too, plus `common.schema.json`. For `architecture`, the example is enough: read a schema only to
  check one field.

The diagram comes from what you know and what you read in the project: read the code that does the thing, don't
guess from names. Leave out `meta.repository` and `sources` (archify's source links need a clean, committed checkout)
unless the user asks for links to the code. If they do, read `archify/references/repository-authoring.md` first.

Pick the type from the question: `workflow` for steps and decisions, `sequence` for who calls whom in order,
`architecture` for what a system is made of, and so on. Leave `meta.visual_preset` out: that is the classic look, the
one that suits Fleet. Set `meta.quality_profile` to `"showcase"`. Keep labels short, the way you would write them on
a whiteboard. Component `type` and boundary `kind` take only the values in the schema's enums (`architecture.schema.json` lists
them), and a boundary's frame must not cover a component that isn't inside it: give each frame its own area.

## 3. Write it outside the repository

Make a folder of its own for this diagram, outside the user's project, and work from it, so archify's `.archify/`
folder and its receipt files never land in the repository:

```bash
dir="$(mktemp -d "${TMPDIR:-/tmp}/fleet-diagrams-XXXXXX")" && cd "$dir" && pwd
```

Save the JSON there as `candidate.json` with `"output": "diagram.html"` in `meta`. Then run archify with the update
check off:

```bash
ARCHIFY_UPDATE_CHECK_DISABLED=1 node <archify folder>/bin/archify.mjs finalize <type> candidate.json diagram.html --quality showcase --json
```

Read the printed receipt:

- **Exit 0**: it passed.
- **Exit 2 with `status: "skipped"` and the diagnostic `viewer/chrome-unavailable`**: there is no Chrome to check
  the page in. The HTML was still written. That counts as a pass. Don't try to install Chrome.
- **`viewer/browser-check-runtime` with "No usable sandbox"**: Chrome is there but can't start. Run the same command
  again with `ARCHIFY_CHROME_NO_SANDBOX=1`.
- **Anything else**: a rule the JSON broke. Fix what the diagnostic names and run the same command again, as
  `ARCHIFY_CHROME_NO_SANDBOX=1` or not as before. Two tries per problem, then tell the user what is wrong.

When a pass reports `visualReviewRecommendation` for crossings, you may move the nodes it names once and rerun with
`--out-dir review-2` (see `## Delivery` in `ARCHIFY.md`). Otherwise leave it.

## 4. Show it

Call `fleet_page_show` with placement `tab`, the absolute path of `diagram.html` and a title for the tab. Fleet puts
its colours, type and light or dark theme on the page, hides archify's toolbar and title, and fits the diagram to the
panel, so don't add any of that yourself. Fix what Fleet's page check reports and show it again.

Say in one or two lines what the diagram shows and anything you left out on purpose. Don't repeat the diagram's text in
the chat, and don't mention archify's update notices.
