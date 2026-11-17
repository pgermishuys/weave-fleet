import { afterEach, describe, expect, it, vi } from "vitest";
import { randomId } from "../random-id";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe("randomId", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("makes a UUID in an insecure context, where crypto.randomUUID is missing", () => {
    vi.stubGlobal("crypto", { getRandomValues: globalThis.crypto.getRandomValues.bind(globalThis.crypto) });
    const a = randomId();
    expect(a).toMatch(UUID);
    expect(randomId()).not.toBe(a);
  });

  it("uses crypto.randomUUID when there is one", () => {
    vi.stubGlobal("crypto", { randomUUID: () => "fixed" });
    expect(randomId()).toBe("fixed");
  });
});
