import { describe, expect, it, vi } from "vitest";
import type { DeviceCredentials } from "@/lib/device-credentials";
import { answerFromNotification, replyForAction } from "../notification-action";
import { actionsFor, answeredNotification, notificationFor, type NotificationData } from "../notification-options";
import { parsePushPayload } from "../payload";

const credentials: DeviceCredentials = {
  homeMachineId: "hangar", homeMachineName: "hangar", homeBaseUrl: "https://hangar.ts.net", deviceId: "d1", token: "fdt_home.1",
  grants: [{ machineId: "falcon", baseUrl: "https://falcon.ts.net", token: "fdt_falcon.1" }], pairedAt: "",
};

const permission = parsePushPayload({
  v: 1, machineId: "falcon", machineName: "falcon", sessionId: "s1", kind: "permission", reason: "needs_you",
  title: "Per-device tokens", body: "Wants to run dotnet test", url: "/phone/s/falcon/s1?ask=p1", tag: "falcon:s1", requestId: "p1",
})!;

const data = notificationFor(permission, 2).options.data as NotificationData;

describe("notification buttons", () => {
  it("offers Allow once and Deny on a permission ask where the platform shows buttons", () => {
    expect(actionsFor(permission, 2).map((a) => a.action)).toEqual(["allow-once", "deny"]);
    expect(notificationFor(permission, 2).options.actions).toHaveLength(2);
  });

  it("offers none on iOS, without a request, or for other kinds", () => {
    expect(actionsFor(permission, 0)).toEqual([]);
    expect(actionsFor({ ...permission, requestId: undefined }, 2)).toEqual([]);
    expect(actionsFor({ ...permission, kind: "question" }, 2)).toEqual([]);
    expect(notificationFor(permission, 0).options.actions).toBeUndefined();
  });

  it("never puts a token in the notification", () => {
    expect(JSON.stringify(notificationFor(permission, 2))).not.toContain("fdt_");
  });

  it("maps buttons to replies", () => {
    expect(replyForAction("allow-once")).toBe("once");
    expect(replyForAction("deny")).toBe("reject");
    expect(replyForAction("")).toBeNull();
  });
});

describe("answerFromNotification", () => {
  it("answers another machine with the phone's own key, no cookies", async () => {
    const fetcher = vi.fn(async () => new Response(null, { status: 204 }));

    const result = await answerFromNotification("allow-once", data, credentials, "https://hangar.ts.net", fetcher as unknown as typeof fetch);

    expect(result).toEqual({ kind: "answered", allowed: true });
    const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("https://falcon.ts.net/api/sessions/s1/permissions/p1");
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer fdt_falcon.1");
    expect(init.credentials).toBe("omit");
    expect(JSON.parse(String(init.body))).toEqual({ reply: "once" });
  });

  it("answers home on this origin", async () => {
    const fetcher = vi.fn(async () => new Response(null, { status: 204 }));
    const home = { ...data, machineId: "hangar" };

    await answerFromNotification("deny", home, credentials, "https://hangar.ts.net", fetcher as unknown as typeof fetch);

    const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/sessions/s1/permissions/p1");
    expect(JSON.parse(String(init.body))).toEqual({ reply: "reject" });
  });

  it("opens the ask when it's already answered, offline, or there's no key", async () => {
    const gone = vi.fn(async () => new Response("{}", { status: 404 }));
    const offline = vi.fn(async () => {
      throw new TypeError("offline");
    });

    expect(await answerFromNotification("allow-once", data, credentials, "x", gone as unknown as typeof fetch)).toEqual({ kind: "open", url: data.url, reason: "gone" });
    expect(await answerFromNotification("allow-once", data, credentials, "x", offline as unknown as typeof fetch)).toMatchObject({ kind: "open", reason: "failed" });
    expect(await answerFromNotification("allow-once", { ...data, machineId: "osprey" }, credentials, "x", gone as unknown as typeof fetch)).toMatchObject({ kind: "open", reason: "no-key" });
  });

  it("replaces the notification with what happened", () => {
    const answered = answeredNotification(data, "falcon", true);
    expect(answered.title).toBe("Allowed — falcon carries on");
    expect(answered.options.tag).toBe("falcon:s1");
    expect(answered.options.data.url).toBe("/phone/answered?machine=falcon&session=s1&reply=once");
  });
});
