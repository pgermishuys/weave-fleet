import { describe, expect, it } from "vitest";
import { browserTarget } from "@/lib/browser-target";

describe("browserTarget", () => {
  it.each([
    ["5173", "http://localhost:5173/"],
    ["localhost:5173", "http://localhost:5173/"],
    ["localhost:5173/cart", "http://localhost:5173/cart"],
    ["127.0.0.1:8080", "http://127.0.0.1:8080/"],
    ["shop.localhost:3000", "http://shop.localhost:3000/"],
    ["https://localhost:7043/swagger", "https://localhost:7043/swagger"],
    [" http://[::1]:4000 ", "http://[::1]:4000/"],
  ])("reads %s as an address", (input, url) => {
    expect(browserTarget(input)).toEqual({ kind: "address", url });
  });

  it.each(["npm run dev", "bun --hot server.ts", "dotnet watch -- --urls http://localhost:$PORT", "python3 -m http.server 8000"])(
    "reads %s as a command",
    (input) => {
      expect(browserTarget(input)).toEqual({ kind: "command", command: input });
    },
  );

  it("has nothing for an empty field", () => {
    expect(browserTarget("   ")).toBeNull();
  });
});
