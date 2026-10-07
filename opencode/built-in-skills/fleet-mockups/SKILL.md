---
name: fleet-mockups
description: Build UI mockups, prototypes and before-and-after comparisons of screens as HTML pages, and show them to the user in a page canvas beside the chat. Use when the user asks for a mockup, wireframe, prototype or design options, or to see an idea before it's built. Not for showing how something works (fleet-explain) or walking through a change (fleet-walkthrough).
---

# Mockups in a page canvas

A mockup answers a question before code is written: which layout, which option, what will it look like. Build it as
a web page and show it beside the chat, so the user can click through it.

## 1. Where it goes

- One folder per topic, one self-contained HTML file per page: CSS and scripts inline, no build step.
- If the project already has a place for mockups, use it. Otherwise use a folder in a temp directory, and ask before
  adding mockups to the repository. Not the project's root or a folder with its `package.json`: Fleet takes a page
  there for the project's own app and refuses it.

## 2. Show it

Call **`fleet_page_show`** with the page's absolute path. Fleet copies the page and the web files in its folder,
serves the copy itself and shows it in a page canvas beside the chat. There's no server to start: don't run
`python -m http.server` or `npx serve`, and don't use `fleet_app_start` for a mockup.

- **After every edit, show it again** with the same path. The same tab updates and reloads for the user.
- **Fix what the check finds.** Each time Fleet shows the page it reports script errors, files that didn't load and
  anything wider than a desktop or a phone window. Fix those and show the page again.
- **One tab per file.** Show another page in the folder with its own path, or link to it from the first page.
- **Links stay inside the folder** and are relative: `styles.css` or `option-b.html`, not `/styles.css` or
  `../shared.css`. Fleet warns you about links that won't load.
- **No storage.** The page runs sandboxed, so `localStorage` and cookies aren't available. Keep state in variables.

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
