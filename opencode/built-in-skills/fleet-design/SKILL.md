---
name: fleet-design
description: Design any HTML page you build, such as mockups, reports, dashboards, explainers and small tools, so it looks chosen for its subject instead of generic. Covers palette, type, layout, light and dark themes, phone width and copy. Use before writing a page's HTML or CSS, together with fleet-mockups when the page is a mockup.
---

# Designing a page

Pages built by an agent tend to look alike. This skill makes each one look chosen for its subject and built with
care, at the level of design the page needs.

## 1. Decide how much design it needs

- **Plain**: a plan, a report, a comparison, a demo. Real type hierarchy, considered spacing and a proper palette,
  nothing more. No big hero, few flourishes.
- **Distinctive**: a landing page, a game, a tool the user will keep or share. Take a point of view (section 5).

When unsure, go plain. A well-set plain page is always fine; an overdesigned one often isn't.

## 2. Start from what exists

The user's words win, then the project's own design system, then your choices. Before choosing anything, look for the
project's tokens or theme file, its main stylesheet, or design notes in AGENTS.md or CLAUDE.md. If the page is part of
an existing app, or a mockup of one, use its colours, fonts, spacing and components. This skill only fills the gaps.

## 3. Plan before writing CSS

Write a short plan first:

- **Colour**: 4 to 6 named hex values, for example ground, surface, text, muted text and accent.
- **Type**: a face for headings, one for body text, and a mono or utility face for data and captions if needed.
- **Layout**: one or two sentences.

Take every colour and type choice from the plan.

Ground it in the subject: who reads the page, and the one job it does. Include at least one detail only this subject
has, such as its real units, terms or conventions, as content rather than decoration. Use real content throughout,
never lorem ipsum.

## 4. Rules for every page

**Type**
- Pair faces on purpose. Google Fonts is fine; always give a fallback stack.
- Keep running text about 65 characters wide. Pick a type scale and keep to it.
- `text-wrap: balance` on headings, a little letter-spacing on uppercase labels, and
  `font-variant-numeric: tabular-nums` wherever digits line up in columns.
- Don't break headings by hand with `<br>`. At another width the break strands a single word on its own line; let
  `text-wrap: balance` place the breaks.

**Colour and themes**
- Tint the greys slightly toward the accent. A pure mid-grey looks like a default nobody chose.
- Every colour is a variable on `:root`. Under `@media (prefers-color-scheme: dark)`, redefine only the variables and
  set `color-scheme: dark`. Never define a colour only inside the dark block.
- Give `body` its background from a variable.
- Design the dark theme instead of inverting the light one. Check contrast, and check the accent works on both grounds.
- Status colours (good, warning, critical) are separate from the accent.

**Layout**
- Space siblings with flex or grid and `gap`, not margins on each element.
- Keep 16px side gutters at every width, set once on `body` or one wrapper. Stack to one column around 400px wide.
  The page never scrolls sideways; only a wide table, code block or diagram may, inside its own `overflow-x: auto` box.
- Long unbroken text is what usually breaks phone width: a shell command, a URL, a hash. Give the grid or flex item
  that holds it `min-width: 0`, and give the `pre` `overflow-x: auto` (or let a URL wrap with
  `overflow-wrap: anywhere`). An install command in a card is the classic case.
- `max-width: 100%` on images.
- Repeated items, like cards in a row or rows in a list, share edges, padding and baselines. Choose a column count the
  items fill, so nothing sits alone in a row or stretches over empty space. Text that can outgrow its box wraps;
  clipped text is a bug.
- Borders, fills, shadows and rounded corners mark something as separate. Use them on the one thing that needs setting
  apart, not on every block. Open with big-number tiles only when the numbers are the point of the page.

**Complete on load**
- Everything meant to be read shows on first load, with no `opacity: 0` waiting for a scroll. Size a hero to its
  content, not to `100vh`.
- A tool opens with example data filled in and labelled as an example, never as an empty shell.

**Structure means something**
- Number sections 01, 02, 03 only when the order matters. Eyebrows, dividers and labels say something true about the
  content.

**Pages people operate** (dashboards, tools)
- Summary first, detail after. Show state in shape as well as colour: a pill, a chip, a stripe. Controls look
  clickable.
- Charts use one scale for marks, ticks and labels. Labels only name values the chart reaches, take their colour from
  the theme variables, and stay inside the drawing.

**Avoid the generic AI look.** Unless the user asks for one of these, don't use:
- a cream or warm off-white ground with an orange or terracotta accent, whatever the typeface
- near-black with a single acid-green or vermilion accent
- a purple-to-blue gradient hero on white
- newspaper hairlines and dense columns
- Inter or Space Grotesk as the safe choice
- emoji as section markers
- everything centred, the same rounded corners on everything, or a coloured bar down the side of rounded cards

**Build it cleanly**
- Visible focus, real buttons and links, and `prefers-reduced-motion` respected.
- Watch selector specificity: a type selector and a class both setting margins will quietly undo your spacing.
- Load a library only when it does real work, as a pinned version from a well-known CDN. Keep your own CSS and JS
  inline.
- For generated art, draw on a canvas instead of writing long SVG paths by hand.

**Copy**
- Name things the way the user knows them: "notifications", not "webhook config".
- Buttons say what they do ("Publish"), and confirmations say what happened ("Published").
- Errors say what went wrong and how to fix it, without apologising.
- Short, plain sentences. No asides between dashes, no "not X, but Y", no scare quotes, no "it's worth noting".
- The page title is a name, two to four words, not a summary.

## 5. Distinctive pages

- Before the plan, write two or three directions in a sentence each, taken from different parts of the subject's world
  (its tools, its places, its history, its people), and pick the one that fits best. Your first idea is the one every
  other agent has too.
- Check the plan against the subject. Change any part you'd have picked for any similar page, and say what you changed.
- Open with the most characteristic thing in the subject's world: a headline, an image, a live demo.
- Type carries the personality. Avoid the faces you'd use on every project.
- Make one bold move and keep everything around it quiet. If the accent fights the ground, shift or desaturate it
  instead of replacing it.
- Motion: one planned moment beats effects scattered everywhere, and none at all is often better.

## 6. Look once, then hand it over

When the page is showing in a browser canvas, take one `fleet_browser_screenshot`, plus one with `viewport: "phone"`
if the layout matters or the page shows a command or code. On the phone shot, look for anything cut off at the right
edge. Fix what the shots show in one pass, then hand it over. Don't loop on screenshots: the user reviews the live
page and asks for more polish if they want it.
