import { afterEach, describe, expect, it, vi } from "vitest";
import { onModTreeInvalid, reportModTreeInvalid } from "@/lib/mods/failures";

const failure = { site: "Pane", reason: "bad", mods: [{ name: "m", draft: false }] } as const;

describe("mod tree failures", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("tells every listener until it leaves", () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const a = vi.fn();
    const b = vi.fn();
    const offA = onModTreeInvalid(a);
    const offB = onModTreeInvalid(b);
    reportModTreeInvalid(failure);
    offA();
    reportModTreeInvalid(failure);
    offB();
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).toHaveBeenCalledTimes(2);
    expect(a).toHaveBeenCalledWith(failure);
  });

  it("warns in development, naming the site, mods and reason", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    reportModTreeInvalid(failure);
    expect(warn).toHaveBeenCalledOnce();
    expect(String(warn.mock.calls[0]![0])).toMatch(/Pane.*m.*bad/);
  });

  it("survives a listener that throws", () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const after = vi.fn();
    const offA = onModTreeInvalid(() => {
      throw new Error("boom");
    });
    const offB = onModTreeInvalid(after);
    expect(() => reportModTreeInvalid(failure)).not.toThrow();
    expect(after).toHaveBeenCalled();
    offA();
    offB();
  });
});
