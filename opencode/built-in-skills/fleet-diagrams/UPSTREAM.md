# Upstream

fleet-diagrams runs **archify** by tt-a1i, vendored in `archify/`:

- Source: https://github.com/tt-a1i/archify (the `archify/` folder of the repository)
- Commit: `b25142d`, version 3.1.0
- License: MIT. The text is in `archify/LICENSE`, and archify's own third-party notices are in
  `archify/THIRD_PARTY_NOTICES.md`. Both ship with the skill.

## What differs from upstream

| Path | Change |
|---|---|
| `archify/SKILL.md` | renamed `archify/ARCHIFY.md`, so a harness that scans skill folders doesn't list a second skill named `archify`; the two links to it in `references/` follow |
| `archify/examples/*.html` | not copied: rendered pages, about 13 MB; the JSON examples stay |
| everything else | unchanged |

`SKILL.md` (this folder) is Fleet's own: it tells the agent what to read, where to write, and how to show the page.
Fleet's theme for archify pages is not in this folder: Fleet adds it when it serves the page
(`src/WeaveFleet.Application/Pages/ArchifyTheme.cs`).

To update, copy the upstream `archify/` folder again, leave out `examples/*.html`, rename `SKILL.md` to `ARCHIFY.md`,
change its links in `references/delivery-contract.md` and `references/repository-authoring.md`, and check that
`ArchifyTheme.Layer` still matches the class names and variables the new version uses.
