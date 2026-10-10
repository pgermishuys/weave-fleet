// One rule for a mod's id, the same one the client applies (src/ids.ts).
import { describe, expect, test } from "bun:test";
import { createHost } from "../src/host";
import { MOD_NAME, parseModId } from "../src/ids";
import { fakeCheck } from "./helpers/fake-check";
import { FakePeer } from "./helpers/fake-peer";

const long = "a".repeat(64);
const uuid = "3f2b8c1e-9d4a-4b6e-8f10-2c7a5d9e1b34";

describe("parseModId", () => {
  const accepted: [string, unknown][] = [
    ["test-chips@v1", { name: "test-chips", version: 1 }],
    ["a@v10", { name: "a", version: 10 }],
    [`${long}@v2`, { name: long, version: 2 }],
    ["x@draft:ses_ABC-123", { name: "x", draft: "ses_ABC-123" }],
    [`x@draft:${uuid}`, { name: "x", draft: uuid }],
    [`x@draft:${"s".repeat(128)}`, { name: "x", draft: "s".repeat(128) }],
  ];
  for (const [id, parsed] of accepted) test(`accepts ${id.slice(0, 40)}`, () => expect(parseModId(id)).toEqual(parsed as any));

  const refused = ["m@v0", "m@v01", `${long}a@v1`, "x@draft:a.b", "x@draft:a/b", "x@draft:../x", "x@draft:", `x@draft:${"s".repeat(129)}`, "1a@v1", "A@v1", "@v1", "m", "m@v", "m@v1\n", "m@v1x"];
  for (const id of refused) test(`refuses ${JSON.stringify(id).slice(0, 40)}`, () => expect(parseModId(id)).toBeUndefined());

  test("the name rule", () => {
    expect(MOD_NAME.test(long)).toBe(true);
    expect(MOD_NAME.test(long + "a")).toBe(false);
    expect(MOD_NAME.test("")).toBe(false);
  });
});

describe("load refuses ids the client refuses", () => {
  for (const id of ["m@v0", "m@v01", "m@draft:../x", "m@draft:a.b"]) {
    test(id, async () => {
      const peer = new FakePeer();
      createHost({ peer, check: fakeCheck, log: () => {} });
      const draft = id.includes("draft");
      const params = draft ? { id, name: "m", version: "draft", sessionId: id.split("draft:")[1], root: "/nowhere" } : { id, name: "m", version: Number(id.split("@v")[1]), root: "/nowhere" };
      await expect(peer.call("load", params)).rejects.toThrow(/neither name@v<n> nor name@draft/);
    });
  }
});
