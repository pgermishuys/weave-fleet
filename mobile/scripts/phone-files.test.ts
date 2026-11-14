import { describe, expect, test } from "bun:test";
import { filesReachingPhone, sharedRuntimeFiles } from "./phone-files-lib";

describe("the shared files the phone runs", () => {
  const shared = sharedRuntimeFiles();

  test("follow the app's run-time imports into the web client, through their own imports", () => {
    expect(shared).toContain("client/src/lib/domain-event-reducer.ts");
    expect(shared).toContain("client/src/components/session/activity-stream-tool-card.ts");
    expect(shared).toContain("client/src/lib/tools/registry.ts");
  });

  test("leave out type-only imports, the shimmed composable and anything in mobile/", () => {
    expect(shared).not.toContain("client/src/api/client.ts");
    expect(shared).not.toContain("client/src/composables/use-session-permissions.ts");
    expect(shared).not.toContain("client/src/composables/use-send-prompt.ts");
    expect(shared.filter((f) => f.startsWith("mobile/") || f.includes("node_modules") || f.endsWith(".d.ts"))).toEqual([]);
  });

  test("a change reaches the phone through mobile/, this workflow or a shared file", () => {
    const changed = [
      "client/src/components/session/SessionView.vue",
      "client/src/api/client.ts",
      "src/WeaveFleet.Api/Program.cs",
      "client/src/lib/domain-event-reducer.ts",
      "mobile/e2e/walk-all.sh",
      ".github/workflows/mobile.yml",
      ".github/workflows/ci.yml",
    ];
    expect(filesReachingPhone(changed, shared)).toEqual([
      "client/src/lib/domain-event-reducer.ts",
      "mobile/e2e/walk-all.sh",
      ".github/workflows/mobile.yml",
    ]);
    expect(filesReachingPhone(["client/src/api/client.ts", "client/src/assets/main.css"], shared)).toEqual([]);
  });
});
