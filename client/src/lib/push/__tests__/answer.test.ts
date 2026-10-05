import { describe, expect, it, vi } from "vitest";
import { permissionAnswerRequest, questionAnswerRequest, questionRejectRequest, sendAnswer } from "../answer";

describe("answer requests", () => {
  it("sends a permission answer to another machine with its token and no cookies", () => {
    const request = permissionAnswerRequest({ baseUrl: "https://falcon.ts.net", token: "fdt_f.1" }, "s 1", "perm/1", "once");
    expect(request.url).toBe("https://falcon.ts.net/api/sessions/s%201/permissions/perm%2F1");
    expect(request.init.method).toBe("POST");
    expect(request.init.credentials).toBe("omit");
    expect((request.init.headers as Record<string, string>).Authorization).toBe("Bearer fdt_f.1");
    expect(JSON.parse(String(request.init.body))).toEqual({ reply: "once" });
  });

  it("sends words with a refusal, and uses the cookie at home", () => {
    const request = permissionAnswerRequest({ baseUrl: "", token: null }, "s1", "p1", "reject", "Run the unit tests instead");
    expect(request.url).toBe("/api/sessions/s1/permissions/p1");
    expect(request.init.credentials).toBe("include");
    expect((request.init.headers as Record<string, string>).Authorization).toBeUndefined();
    expect(JSON.parse(String(request.init.body))).toEqual({ reply: "reject", message: "Run the unit tests instead" });
  });

  it("answers and rejects questions with the desktop's bodies", () => {
    const answer = questionAnswerRequest({ baseUrl: "", token: null }, "s1", "call-1", [["Plain 401"]]);
    expect(answer.url).toBe("/api/sessions/s1/questions/call-1/answer");
    expect(JSON.parse(String(answer.init.body))).toEqual({ answers: [["Plain 401"]] });
    expect(questionRejectRequest({ baseUrl: "", token: null }, "s1", "call-1").url).toBe("/api/sessions/s1/questions/call-1/reject");
  });

  it("reads the outcome", async () => {
    const respond = (status: number, body: string | null = "{}") => vi.fn(async () => new Response(body, { status })) as unknown as typeof fetch;
    const request = permissionAnswerRequest({ baseUrl: "", token: null }, "s1", "p1", "once");

    expect(await sendAnswer(request, respond(204, null))).toEqual({ ok: true, gone: false, error: null });
    expect(await sendAnswer(request, respond(404))).toMatchObject({ ok: false, gone: true });
    expect(await sendAnswer(request, respond(400, JSON.stringify({ error: "Answer once, always or reject." })))).toMatchObject({ error: "Answer once, always or reject." });
    expect(await sendAnswer(request, (async () => { throw new TypeError("offline"); }) as unknown as typeof fetch)).toMatchObject({ error: "Couldn't reach the machine." });
  });
});
