import type { Peer } from "../../src/host";

export interface Call {
  method: string;
  params: any;
}

/** A Peer that records what the host sends and answers the host's requests from `answers`. */
export class FakePeer implements Peer {
  handlers = new Map<string, (params: any) => unknown>();
  notifications: Call[] = [];
  requests: Call[] = [];
  store = new Map<string, unknown>();
  answers: Record<string, (params: any) => unknown> = {
    "store.get": (p) => ({ value: this.store.get(`${p.mod}|${p.key}`) }),
    "store.set": (p) => (this.store.set(`${p.mod}|${p.key}`, p.value), {}),
    "store.delete": (p) => (this.store.delete(`${p.mod}|${p.key}`), {}),
    "store.keys": (p) => ({ keys: [...this.store.keys()].filter((k) => k.startsWith(`${p.mod}|`)).map((k) => k.split("|")[1]) }),
    "ui.open": () => ({}),
    "ui.close": () => ({}),
    "ui.toast": () => ({}),
    "session.get": (p) => ({ id: p.sessionId, title: "Invented title", harness: "opencode", cwd: "/work/demo", surfaces: ["desktop"] }),
  };

  handle(method: string, handler: (params: any) => unknown): void {
    this.handlers.set(method, handler);
  }
  async request(method: string, params: unknown): Promise<unknown> {
    this.requests.push({ method, params });
    const answer = this.answers[method];
    if (!answer) throw new Error(`no answer for ${method}`);
    return await answer(params);
  }
  notify(method: string, params: unknown): void {
    this.notifications.push({ method, params });
  }

  /** Fleet calling the host. */
  call(method: string, params: unknown = {}): Promise<any> {
    const h = this.handlers.get(method);
    if (!h) throw new Error(`host has no handler for ${method}`);
    return Promise.resolve().then(() => h(params));
  }
  notes(method: string): any[] {
    return this.notifications.filter((n) => n.method === method).map((n) => n.params);
  }
  reqs(method: string): any[] {
    return this.requests.filter((n) => n.method === method).map((n) => n.params);
  }
}
