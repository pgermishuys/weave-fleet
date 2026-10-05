/**
 * The maths behind the phone's touch gestures (sheets that drag between detents, swipe back, swipe a row to
 * archive, pull to refresh), ported from the approved mockups' kit.js so the feel stays the same. Positions are px,
 * times ms, velocities px per ms. No DOM.
 */

/** A touch position over time: [time, position]. */
export type Sample = readonly [time: number, position: number];

export const clamp = (value: number, min: number, max: number): number => Math.min(max, Math.max(min, value));

/** iOS-style rubber banding: the further past the edge, the less it follows the finger. */
export function rubber(distance: number, dimension: number, constant = 0.55): number {
  if (dimension <= 0) return 0;
  return (1 - 1 / ((distance * constant) / dimension + 1)) * dimension;
}

/** Speed over the last few samples, px per ms (positive: down / right). */
export function velocity(samples: readonly Sample[]): number {
  if (samples.length < 2) return 0;
  const [t0, p0] = samples[0];
  const [t1, p1] = samples[samples.length - 1];
  return (p1 - p0) / Math.max(1, t1 - t0);
}

/** Keeps the last five samples, as a gesture's moves come in. */
export function addSample(samples: readonly Sample[], sample: Sample, keep = 5): Sample[] {
  const next = [...samples, sample];
  return next.length > keep ? next.slice(next.length - keep) : next;
}

/** Which way a touch is going once it's moved past the slop: along x (by `ratio` or more), along y, or not yet. */
export function axisIntent(dx: number, dy: number, ratio = 1.4, slop = 8): "x" | "y" | null {
  if (Math.abs(dx) < slop && Math.abs(dy) < slop) return null;
  return Math.abs(dx) > Math.abs(dy) * ratio ? "x" : "y";
}

export type Detent = "fit" | "medium" | "large";

/**
 * Where each detent puts the sheet, as a translateY from fully open. A sized-to-content sheet has one stop at 0; a
 * medium/large one is as tall as the screen allows and medium shows its top 52% of the screen.
 */
export function detentOffsets(detents: readonly Detent[], sheetHeight: number, screenHeight: number): Partial<Record<Detent, number>> {
  if (detents.includes("fit")) return { fit: 0 };
  const offsets: Partial<Record<Detent, number>> = {};
  if (detents.includes("large")) offsets.large = 0;
  if (detents.includes("medium")) offsets.medium = Math.max(0, Math.round(sheetHeight - screenHeight * 0.52));
  return offsets;
}

/**
 * Where a dragged sheet goes when it's let go: projected along its speed, it closes once it would travel past half
 * of what's left below its lowest stop (or is flicked down near it), otherwise it settles on the nearest stop.
 */
export function sheetRelease(input: { stops: readonly number[]; current: number; velocity: number; dismissAt: number }): { close: true } | { close: false; to: number; ms: number } {
  const stops = [...input.stops].sort((a, b) => a - b);
  const lowest = stops[stops.length - 1] ?? 0;
  const projected = input.current + input.velocity * 220;
  if (projected > lowest + (input.dismissAt - lowest) * 0.5 || (input.velocity > 0.9 && input.current > lowest - 40)) return { close: true };
  let best = stops[0] ?? 0;
  for (const stop of stops) if (Math.abs(stop - projected) < Math.abs(best - projected)) best = stop;
  const ms = clamp(Math.abs(best - input.current) / Math.max(Math.abs(input.velocity), 1.2), 220, 420);
  return { close: false, to: best, ms };
}

/** Swipe back: goes back on a flick or past 45% of the width, and finishes at the finger's speed. */
export function swipeBackRelease(input: { dx: number; width: number; velocity: number }): { back: true; ms: number } | { back: false } {
  const { dx, width, velocity: v } = input;
  if ((v > 0.35 || dx > width * 0.45) && v > -0.15) {
    return { back: true, ms: clamp((width - dx) / Math.max(v, 1.2), 160, 380) };
  }
  return { back: false };
}

/** How long the pop animation takes from `fromX`: as fast as the finger was going, between 160 and 380 ms. */
export function popDuration(width: number, fromX: number, speed: number): number {
  return clamp(speed > 0 ? (width - fromX) / speed : 380, 160, 380);
}

/** Swiping a row left: all the way past 60% archives it; past half the action's width (slowly) leaves it open. */
export function swipeRowRelease(input: { x: number; width: number; velocity: number; actionWidth: number }): "commit" | "open" | "close" {
  if (input.x < -input.width * 0.6) return "commit";
  if (input.x < -input.actionWidth / 2 && input.velocity < 0.3) return "open";
  return "close";
}

/** Pull to refresh: how far the content follows a pull of `dy`, and whether letting go now refreshes. */
export const PULL_THRESHOLD = 72;
export function pullDistance(dy: number): number {
  return dy <= 0 ? 0 : rubber(dy, 260, 0.9);
}
