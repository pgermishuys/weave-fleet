---
name: fleet-explain
description: Show how something works over time as a short animation, 30 seconds at most, in a browser canvas beside the chat. For a flow, a request moving through layers, a change as data moving, a race or a retry. Use when the user asks to see, or be shown, how something works or flows, or asks for an animation. Not for static structure (use the diagram canvas) or reading a diff (fleet-walkthrough).
---

# Show it with a short animation

A diagram shows what's connected. An animation shows what happens, in what order, and where it can go wrong. Build
one when the user wants to see it, and the order is the point.

## 1. Find the one thing that moves

Read the code first. Trace one real thing end to end: a request, an event, an edit, a job. Name each place it stops,
with the file and function that handles it. That thing is the only thing that moves; everything else stays still.

Find the one surprise worth showing: where it's dropped, waits, branches, loops or races. Animate it, don't only
caption it: a second thing that takes the other branch and is turned away says more than a sentence.

## 2. Write the script before any code

Four to seven beats, **30 seconds at most in total**, each 3 to 6 seconds:

```
0–4 s   The agent ticks task 2          (plan.md)
4–9 s   OpenCode reports the edit        (OpenCodeMapper.TryMapFilesWritten)
...
```

Each beat has a short title (it's also the step button's label, so write it whole, don't cut it), one or two plain
sentences, and where it is in the code. If it doesn't fit in 30 seconds, explain less, or make two animations.

## 3. Build it

One self-contained HTML file in a folder of its own, in a temp directory unless the user wants it in the repository.
No build step and no animation library; SVG and a little JavaScript are enough.

- **Draw every frame from time.** Put `const TOTAL = 30;` (or less) at the top. Write one `render(t)` that sets every
  element from `t` alone, and drive it with `requestAnimationFrame`. Scrubbing, pausing, stepping and stills then
  work for free, and nothing drifts.
- **Move with `transform` and `opacity`.** Browsers animate those without layout or repaint work, so they stay smooth.
- **One SVG unit is one pixel.** Lay the drawing out in JavaScript from the stage's real width, with the `viewBox`
  width equal to that width, and redo the layout when the width changes (a `ResizeObserver`). Never draw a fixed wide
  scene and let the browser shrink it: its text becomes unreadable. Stations go in one row when they fit at 150px or
  more each, two rows when they don't, and one column on a phone.
- **Text in the drawing is 13px or larger**, everywhere, phone included. Station names are short; put detail in the
  caption.
- **The moving thing never covers text.** Give each station an empty spot for it, such as a port on its edge, and
  move it along the wires between stations.
- **Fill the frame.** The drawing uses the stage's width, and the stage is as tall as the drawing, not the window:
  no wide empty bands above or below it.
- **Captions carry the words.** The caption under the drawing changes with each beat: the title, the sentences, and
  "In the code:" with the names.
- **Controls:** Play/Pause (Replay at the end), previous and next step, a scrubber with the time, and the beats as a
  list the user can click. A step pauses on that beat's key moment, where the drawing shows what the caption says.
- **Stills by address:** `?t=17.2` opens paused at that time, and `?step=4` at step 4's key moment.
- **Reduced motion:** with `prefers-reduced-motion`, don't play by itself; the step buttons jump between moments.
- **No sideways scrolling** at 375px: the page is exactly as wide as the screen.
- **Real names, real values:** the file names, event names and limits from the code, like `MinimumPlanSteps = 3`.
- **Design:** follow fleet-design if it's on. Colours as variables with a dark theme; the moving thing gets the one
  strong colour.

## 4. Show it and check it

Serve the folder with **`fleet_app_start`** (`python3 -m http.server $PORT --bind 127.0.0.1 --directory <folder>`).
Fleet shows it in a browser canvas beside the chat.

Take **`fleet_browser_screenshot`** stills with `path: "?step=N"`: two key steps at desktop, and one with
`viewport: "phone"`. Each must pass all of these:

- [ ] The moving thing is where the caption says, and it doesn't cover any text.
- [ ] Every label in the drawing is at least as large as the caption's small print. "Small but legible" is a fail.
- [ ] The drawing fills the frame's width, with no empty bands above or below it, and nothing overlaps or is cut off.
- [ ] On the phone, the stations are in a column and there's no sideways scrolling.
- [ ] `TOTAL` is 30 or less.

If one fails, fix the page and take that still again. Don't hand over a page that fails a check. Each screenshot
costs context, so shoot only what you need to decide.

## 5. Hand it over

Say in a sentence what it shows and how long it runs, and name the surprise ("a 2-step plan never reaches Progress").
Offer to change the depth or make a second one for the part it left out.

## Not this skill

- What connects to what, with no order: Fleet's diagram canvas (`fleet_canvas_open`).
- Reading a diff or pull request: fleet-walkthrough. An animation can follow one when the change is to a flow.
- How a screen will look: fleet-mockups.
- A question the user only wants answered: answer it in words, and offer the animation if the order matters.
