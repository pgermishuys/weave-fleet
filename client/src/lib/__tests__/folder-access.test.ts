import { beforeEach, describe, expect, it, vi } from "vitest";

const post = vi.hoisted(() => vi.fn());
const get = vi.hoisted(() => vi.fn());

vi.mock("@/api/client", () => ({ api: { POST: post, GET: get } }));

import { cloneRepository, createFolder, FolderExistsError, listWorkspaceRoots } from "@/lib/folder-access";

function streamOf(...chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk));
      }
      controller.close();
    },
  });
}

const done = { type: "done", folder: { path: "/src/recipe-box", isGitRepo: true, addedToFleet: false, warning: null } };

beforeEach(() => {
  post.mockReset();
});

describe("cloneRepository", () => {
  it("reports each progress line, even one split across chunks, and resolves with the folder", async () => {
    const progress = JSON.stringify({ type: "progress", phase: "Receiving objects", percent: 38 });
    post.mockResolvedValue({
      data: streamOf(progress.slice(0, 20), `${progress.slice(20)}\n`, `${JSON.stringify(done)}\n`),
      response: new Response(null, { status: 200 }),
    });
    const onProgress = vi.fn();

    const folder = await cloneRepository("pgermishuys/recipe-box", "/src/recipe-box", onProgress);

    expect(onProgress).toHaveBeenCalledWith({ phase: "Receiving objects", percent: 38 });
    expect(folder).toEqual(done.folder);
    expect(post).toHaveBeenCalledWith("/api/directories/clone", {
      body: { repository: "pgermishuys/recipe-box", path: "/src/recipe-box" },
      parseAs: "stream",
    });
  });

  it("fails with git's message when the clone fails part way", async () => {
    post.mockResolvedValue({
      data: streamOf(`${JSON.stringify({ type: "error", error: "Couldn't clone: repository not found" })}\n`),
      response: new Response(null, { status: 200 }),
    });

    await expect(cloneRepository("a/b", "/src/b", vi.fn())).rejects.toThrow("Couldn't clone: repository not found");
  });

  it("fails when the stream ends without an answer", async () => {
    post.mockResolvedValue({ data: streamOf(""), response: new Response(null, { status: 200 }) });

    await expect(cloneRepository("a/b", "/src/b", vi.fn())).rejects.toThrow("The clone stopped before it finished.");
  });

  it("says when the folder is already there", async () => {
    post.mockResolvedValue({ error: { error: "/src/b already exists." }, response: new Response(null, { status: 409 }) });

    const failure = await cloneRepository("a/b", "/src/b", vi.fn()).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(FolderExistsError);
    expect((failure as FolderExistsError).path).toBe("/src/b");
  });
});

describe("createFolder", () => {
  it("shows the server's reason", async () => {
    post.mockResolvedValue({ error: { error: "New folders aren't available in cloud mode." }, response: new Response(null, { status: 400 }) });

    await expect(createFolder("/src/x", true)).rejects.toThrow("New folders aren't available in cloud mode.");
  });
});

describe("listWorkspaceRoots", () => {
  it("lists the roots that exist, in the server's shape", async () => {
    get.mockResolvedValue({
      data: { roots: [{ path: "/src", exists: true, id: "1", source: "user" }, { path: "/gone", exists: false, id: null, source: "env" }] },
      response: new Response(null, { status: 200 }),
    });

    await expect(listWorkspaceRoots()).resolves.toEqual(["/src"]);
  });
});
