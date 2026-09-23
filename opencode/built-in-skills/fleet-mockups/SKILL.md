---
name: fleet-mockups
description: Build UI mockups, prototypes, before-and-after comparisons and visual explainers as HTML pages, and show them to the user in a browser canvas beside the chat. Use when the user asks for a mockup, wireframe, prototype or design options, or to see an idea before it's built.
---

# Mockups in the browser canvas

A mockup answers a question before code is written: which layout, which option, what will it look like. Build it as
a web page and show it beside the chat, so the user can click through it.

## 1. Where it goes

- One folder per topic, one self-contained HTML file per page: CSS and scripts inline, no build step.
- If the project already has a place for mockups, use it. Otherwise use a folder in a temp directory, and ask before
  adding mockups to the repository.

## 2. Show it

Serve the folder with **`fleet_app_start`**. Fleet sets `PORT`, and the command runs in a shell:

```
python3 -m http.server $PORT --bind 127.0.0.1 --directory /path/to/mockups/topic
```

or, with Node, `npx --yes serve -l $PORT /path/to/mockups/topic`. On Windows, write `%PORT%`.

Fleet shows the page in a browser canvas beside the chat. The server reads the files on every request, so after an
edit the user only needs to reload the canvas. Show the other pages in the same folder with `fleet_browser_open` and
`http://localhost:<port>/other.html`, using the port `fleet_app_start` returned.

## 3. Make it look real

- **Start from the real app.** When the mockup changes an existing app, read its stylesheet and copy the real colour
  variables, fonts, spacing and components. A mockup in a generic style can't show whether the idea fits.
- **Design it.** For a new page, or the gaps the app doesn't cover, follow fleet-design if it's turned on: palette,
  type, layout and the generic looks to avoid.
- **Real content.** Use real-looking names and lengths, and include the awkward cases: a long title, an empty list, an
  error, a loading state. Not lorem ipsum.
- **Compare on the same content.** For before-and-after, or for options A, B and C, show them side by side or behind a
  toggle, all rendering the same data, and name what differs.
- **Colours as variables** on `:root`, with a dark theme under `@media (prefers-color-scheme: dark)`. Match the app's
  own theme if it has one.
- **Phone width.** It should work at 375px wide: 16px side gutters, no sideways scrolling.
- **Interactive only where it answers the question:** a toggle between options, a hover or open state. It's a mockup,
  not the app.
- **Accessible basics:** readable contrast, visible focus, real buttons and links.
- Keep everything inline where you can. If you need a library, load it from a well-known CDN.

## 4. Diagrams aren't mockups

Use Fleet's diagram canvas (`fleet_canvas_open`) for flows, architecture and sequence diagrams, not an HTML page.

## 5. Hand it over

Tell the user what they're looking at, what to compare, and what you need from them: "Option B keeps the sidebar; A
folds it into a menu. Which way?" Keep the mockup up while they decide.
