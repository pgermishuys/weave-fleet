---
name: fleet-explain
description: Show how something works over time as a short animation, 30 seconds at most, in a browser canvas beside the chat. For a flow, a request moving through layers, a change as data moving, a race or a retry. Use when the user asks to see, or be shown, how something works or flows, or asks for an animation. Not for static structure (use the diagram canvas) or reading a diff (fleet-walkthrough).
---

# Show it with a short animation

A diagram shows what's connected. An animation shows what happens, in what order, and where it can go wrong. Build
one when the user wants to see it, and the order is the point.

**Being right comes before looking good.** A wrong animation is worse than none: it looks certain, and people remember
the picture, not the caveat. Every step, arrow, order and value on screen must be true to the source. Polish never
makes up for a claim the source doesn't support.

## 1. Pin down the question and the source

Write the user's question in one sentence, and what they should understand when the animation ends. The animation
answers that question and nothing else. If they asked what happens when a step needs them, that moment is the centre
of the animation, not one beat among six.

Find the source and read it properly, not a skim:

- **Code:** the code in the repository that does the thing. Follow the calls; don't guess from names.
- **Material the user gave:** an article, doc, spec or link. That is the authority, not what you know in general. If
  they asked about the article, explain the article, even where you'd have put it differently.

## 2. Find the one thing that moves

Trace one real thing end to end: a request, an event, an edit, a job. Name each place it stops, with the file and
function that handles it (or the section of the article). That thing is the only thing that moves; everything else
stays still.

If the source has a surprise, show it: where it's dropped, waits, branches, loops or races. Animate it, don't only
caption it: a second thing that takes the other branch and is turned away says more than a sentence. Don't invent a
surprise the source doesn't have.

## 3. Write the script, and check every claim, before any code

Four to seven beats, **30 seconds at most in total**, each 3 to 6 seconds:

```
0–4 s   The agent ticks task 2          (plan.md)
4–9 s   OpenCode reports the edit        (OpenCodeMapper.TryMapFilesWritten, OpenCodeMapper.cs:212)
...
```

Each beat has a short title (it's also the step button's label, so write it whole, don't cut it), one or two plain
sentences, and where it is in the source: a file, function and line, or the article's heading and a quoted sentence.
If it doesn't fit in 30 seconds, explain less, or make two animations.

Then go back to the source and check the script, beat by beat. Open each place it cites and confirm it says what the
beat says. Check hardest what an animation makes look certain:

- **Order.** What happens first. What's awaited and what isn't; what runs at the same time and what runs one after
  another; what happens on another thread, process or machine.
- **Who does it.** Which component, process or actor, by its real name.
- **Branches.** Which path the animation shows, and the exact condition that sends it there.
- **Names and values**, spelled as the source spells them: events, limits, timeouts, counts.

A claim you can't find in the source: cut it. If the story can't do without it, say in that beat's caption that it's a
simplification. Don't fill gaps from general knowledge or from what a name suggests. Where running something is cheap
and settles a doubt (a test, a log line), run it.

If, after reading, the source doesn't give a clear story in order, say so, and answer in words instead.

## 4. Build it

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
- **The drawing makes claims too.** A station says the component exists; an arrow says who calls whom; two things
  moving together say they run at the same time; something inside a box says it belongs there. Draw only what the
  checked script says. Take the captions, names and values from the script word for word.
- **Design:** follow fleet-design if it's on. Colours as variables with a dark theme; the moving thing gets the one
  strong colour.

## 5. Show it and check it

Serve the folder with **`fleet_app_start`** (`python3 -m http.server $PORT --bind 127.0.0.1 --directory <folder>`).
Fleet shows it in a browser canvas beside the chat.

First check what it says. Read the beats in the finished page against the script and the source, and pass all of
these:

- [ ] It answers the question from step 1, and the part the user asked about gets the most time.
- [ ] Every beat's title, sentences, names and values match the checked script, and the script matches the source.
- [ ] Every station, arrow and ordering in the drawing is in the checked script. Nothing was added to fill space or
      to look good.
- [ ] Anything simplified says so in its caption.

Then check how it looks. Take **`fleet_browser_screenshot`** stills with `path: "?step=N"`: two key steps at desktop,
and one with `viewport: "phone"`. Each must pass all of these:

- [ ] The moving thing is where the caption says, and it doesn't cover any text.
- [ ] Every label in the drawing is at least as large as the caption's small print. "Small but legible" is a fail.
- [ ] The drawing fills the frame's width, with no empty bands above or below it, and nothing overlaps or is cut off.
- [ ] On the phone, the stations are in a column and there's no sideways scrolling.
- [ ] `TOTAL` is 30 or less.

If one fails, fix the page and take that still again. Don't hand over a page that fails a check. Each screenshot
costs context, so shoot only what you need to decide.

## 6. Hand it over

Say in a sentence what it shows and how long it runs, and name the surprise ("a 2-step plan never reaches Progress").
Say what it's based on (the files, or the article), and name anything it simplifies or that you couldn't confirm.
Offer to change the depth or make a second one for the part it left out.

## Not this skill

- What connects to what, with no order: Fleet's diagram canvas (`fleet_canvas_open`).
- Reading a diff or pull request: fleet-walkthrough. An animation can follow one when the change is to a flow.
- How a screen will look: fleet-mockups.
- A question the user only wants answered: answer it in words, and offer the animation if the order matters.
