import { describe, expect, it } from "vitest";
import {
  currentWindows,
  percentLabel,
  resetLabel,
  toHarnessUsage,
  windowLabel,
  windowToFlag,
} from "@/lib/usage-limits";

const now = Date.parse("2026-10-07T10:00:00Z");
const at = (hours: number) => new Date(now + hours * 3_600_000).toISOString();

function usage(windows: Array<Record<string, unknown>>) {
  return toHarnessUsage({ harnessType: "claude-code", windows, updatedAt: at(0) });
}

describe("usage limits", () => {
  it("reads the server's shape, clamping use to 0–1", () => {
    const read = usage([{ window: "five_hour", utilization: 1.4, resetsAt: at(2), status: "rejected" }, { nope: true }]);
    expect(read).toEqual({
      harnessType: "claude-code",
      windows: [{ window: "five_hour", utilization: 1, resetsAt: Date.parse(at(2)), status: "rejected" }],
    });
    expect(toHarnessUsage({ windows: [] })).toBeNull();
  });

  it("orders the windows shortest first and leaves out the ones that reset", () => {
    const read = usage([
      { window: "seven_day_opus", utilization: 0.2, resetsAt: at(50) },
      { window: "seven_day", utilization: 0.6, resetsAt: at(50) },
      { window: "five_hour", utilization: 0.4, resetsAt: at(-1) },
    ]);
    expect(currentWindows(read, now).map((w) => w.window)).toEqual(["seven_day", "seven_day_opus"]);
  });

  it("names the windows and when they reset", () => {
    expect(windowLabel("five_hour")).toBe("5-hour limit");
    expect(windowLabel("seven_day")).toBe("Weekly limit");
    expect(windowLabel("seven_day_opus")).toBe("Weekly Opus limit");
    // In the viewer's time zone: within a day the time, within a week the day, after that the date.
    const local = (hours: number, options: Intl.DateTimeFormatOptions) => new Date(at(hours)).toLocaleString("en-GB", options);
    expect(resetLabel(Date.parse(at(2)), now, "en-GB")).toBe(`resets ${local(2, { hour: "2-digit", minute: "2-digit" })}`);
    expect(resetLabel(Date.parse(at(48)), now, "en-GB")).toBe(`resets ${local(48, { weekday: "short" })}`);
    expect(resetLabel(Date.parse(at(24 * 9)), now, "en-GB")).toBe(`resets ${local(24 * 9, { day: "numeric", month: "short" })}`);
    expect(resetLabel(null, now)).toBeNull();
  });

  it("says a used-up window is at 100% whatever its last reading", () => {
    const [window] = usage([{ window: "five_hour", utilization: 0.97, resetsAt: at(2), status: "rejected" }])!.windows;
    expect(percentLabel(window)).toBe("100%");
  });

  it("flags only a window at 80% or more, or used up, the worst first", () => {
    expect(windowToFlag(usage([{ window: "five_hour", utilization: 0.79, resetsAt: at(2) }]), now)).toBeNull();
    expect(windowToFlag(usage([
      { window: "five_hour", utilization: 0.81, resetsAt: at(2) },
      { window: "seven_day", utilization: 0.95, resetsAt: at(60) },
    ]), now)?.window).toBe("seven_day");
    expect(windowToFlag(usage([
      { window: "five_hour", utilization: 0.5, resetsAt: at(2), status: "rejected" },
      { window: "seven_day", utilization: 0.95, resetsAt: at(60) },
    ]), now)?.window).toBe("five_hour");
  });
});
