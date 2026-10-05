import { describe, expect, it } from "vitest";
import { addSample, axisIntent, detentOffsets, popDuration, pullDistance, rubber, sheetRelease, swipeBackRelease, swipeRowRelease, velocity } from "../gestures";

describe("gesture maths", () => {
  it("rubber-bands: follows less the further it goes, never past the dimension", () => {
    expect(rubber(0, 300)).toBe(0);
    expect(rubber(100, 300)).toBeLessThan(100);
    expect(rubber(10_000, 300)).toBeLessThan(300);
    expect(rubber(200, 300)).toBeGreaterThan(rubber(100, 300));
  });

  it("measures speed across the samples it keeps", () => {
    let samples = addSample([], [0, 0]);
    for (let t = 10; t <= 100; t += 10) samples = addSample(samples, [t, t * 2]);
    expect(samples).toHaveLength(5);
    expect(velocity(samples)).toBeCloseTo(2);
    expect(velocity([[0, 5]])).toBe(0);
  });

  it("waits for the slop before deciding a direction", () => {
    expect(axisIntent(3, 4)).toBeNull();
    expect(axisIntent(30, 5)).toBe("x");
    expect(axisIntent(12, 10)).toBe("y");
    expect(axisIntent(12, 10, 1.1)).toBe("x");
  });

  it("puts a medium sheet so its top 52% of the screen shows", () => {
    expect(detentOffsets(["fit"], 400, 844)).toEqual({ fit: 0 });
    expect(detentOffsets(["medium", "large"], 780, 844)).toEqual({ large: 0, medium: 341 });
  });

  it("settles a dragged sheet on the nearest stop, or closes it when flicked or dragged low", () => {
    const stops = [0, 341];
    expect(sheetRelease({ stops, current: 60, velocity: 0, dismissAt: 790 })).toEqual({ close: false, to: 0, ms: 220 });
    expect(sheetRelease({ stops, current: 300, velocity: 0.1, dismissAt: 790 })).toMatchObject({ close: false, to: 341 });
    expect(sheetRelease({ stops, current: 320, velocity: 1.2, dismissAt: 790 })).toEqual({ close: true });
    expect(sheetRelease({ stops, current: 600, velocity: 0, dismissAt: 790 })).toEqual({ close: true });
    // a quick flick up from medium goes to large
    expect(sheetRelease({ stops, current: 330, velocity: -1.5, dismissAt: 790 })).toMatchObject({ close: false, to: 0 });
  });

  it("goes back on a flick or past 45%, not on a slow short drag or one reversing", () => {
    expect(swipeBackRelease({ dx: 60, width: 390, velocity: 0.8 })).toMatchObject({ back: true });
    expect(swipeBackRelease({ dx: 200, width: 390, velocity: 0 })).toMatchObject({ back: true });
    expect(swipeBackRelease({ dx: 120, width: 390, velocity: 0.1 })).toEqual({ back: false });
    expect(swipeBackRelease({ dx: 300, width: 390, velocity: -0.4 })).toEqual({ back: false });
    expect(popDuration(390, 0, 0)).toBe(380);
    expect(popDuration(390, 300, 2)).toBe(160);
  });

  it("archives a row swiped all the way, leaves it open past half the action, else closes it", () => {
    expect(swipeRowRelease({ x: -260, width: 390, velocity: 0, actionWidth: 88 })).toBe("commit");
    expect(swipeRowRelease({ x: -60, width: 390, velocity: 0.1, actionWidth: 88 })).toBe("open");
    expect(swipeRowRelease({ x: -60, width: 390, velocity: 0.6, actionWidth: 88 })).toBe("close");
    expect(swipeRowRelease({ x: -20, width: 390, velocity: 0, actionWidth: 88 })).toBe("close");
  });

  it("pulls with resistance", () => {
    expect(pullDistance(-20)).toBe(0);
    expect(pullDistance(100)).toBeGreaterThan(50);
    expect(pullDistance(100)).toBeLessThan(100);
  });
});
