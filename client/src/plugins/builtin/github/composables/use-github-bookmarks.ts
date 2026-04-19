import { onMounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { BookmarkedRepo } from "./github-types";

const BOOKMARKS_API = "/api/integrations/github/bookmarks";
const LEGACY_BOOKMARKS_STORAGE_KEY = "weave:github:repos";

const bookmarks = ref<BookmarkedRepo[]>([]);
const isLoading = shallowRef(false);
const error = shallowRef<string | null>(null);
const hasLoaded = shallowRef(false);

let loadPromise: Promise<void> | null = null;

export interface UseGitHubBookmarksResult {
  bookmarks: Readonly<Ref<readonly BookmarkedRepo[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | null>>;
  refresh: () => Promise<void>;
  replaceBookmarks: (repos: readonly BookmarkedRepo[]) => Promise<void>;
  addBookmark: (repo: BookmarkedRepo) => Promise<void>;
  removeBookmark: (fullName: string) => Promise<void>;
  hasBookmark: (fullName: string) => boolean;
}

function sortByName(repos: readonly BookmarkedRepo[]): BookmarkedRepo[] {
  return [...repos].sort((left, right) => left.fullName.localeCompare(right.fullName));
}

async function readErrorMessage(response: Response, fallback: string): Promise<string> {
  const payload = (await response.json().catch(() => ({}))) as { error?: string; message?: string };
  return payload.error ?? payload.message ?? fallback;
}

function normalizeBookmarksResponse(payload: unknown): BookmarkedRepo[] {
  if (Array.isArray(payload)) {
    return payload as BookmarkedRepo[];
  }

  if (
    payload
    && typeof payload === "object"
    && "bookmarks" in payload
    && Array.isArray((payload as { bookmarks?: unknown }).bookmarks)
  ) {
    return (payload as { bookmarks: BookmarkedRepo[] }).bookmarks;
  }

  return [];
}

function readLegacyBookmarks(): BookmarkedRepo[] {
  if (typeof window === "undefined") {
    return [];
  }

  try {
    const raw = localStorage.getItem(LEGACY_BOOKMARKS_STORAGE_KEY);
    if (!raw) {
      return [];
    }

    const payload = JSON.parse(raw) as BookmarkedRepo[];
    return Array.isArray(payload) ? payload : [];
  } catch {
    return [];
  }
}

function clearLegacyBookmarks(): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    localStorage.removeItem(LEGACY_BOOKMARKS_STORAGE_KEY);
  } catch {
    // localStorage unavailable
  }
}

async function replaceBookmarks(repos: readonly BookmarkedRepo[]): Promise<void> {
  const nextBookmarks = sortByName(repos);
  const response = await apiFetch(BOOKMARKS_API, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ bookmarks: nextBookmarks }),
  });
  if (!response.ok) {
    throw new Error(await readErrorMessage(response, "Failed to sync bookmarks."));
  }

  bookmarks.value = nextBookmarks;
  error.value = null;
}

async function loadBookmarks(): Promise<void> {
  if (loadPromise) {
    await loadPromise;
    return;
  }

  loadPromise = (async () => {
    isLoading.value = true;

    try {
      const response = await apiFetch(BOOKMARKS_API);
      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Failed to fetch bookmarks from server."));
      }

      const serverBookmarks = sortByName(normalizeBookmarksResponse(await response.json()));
      const legacyBookmarks = sortByName(readLegacyBookmarks());

      if (legacyBookmarks.length === 0) {
        bookmarks.value = serverBookmarks;
        error.value = null;
        hasLoaded.value = true;
        return;
      }

      const mergedBookmarks = sortByName([
        ...serverBookmarks,
        ...legacyBookmarks.filter(
          (legacyBookmark) => !serverBookmarks.some((serverBookmark) => serverBookmark.fullName === legacyBookmark.fullName),
        ),
      ]);

      if (mergedBookmarks.length !== serverBookmarks.length) {
        await replaceBookmarks(mergedBookmarks);
      } else {
        bookmarks.value = serverBookmarks;
        error.value = null;
      }

      clearLegacyBookmarks();
      hasLoaded.value = true;
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to fetch bookmarks from server.";
    } finally {
      isLoading.value = false;
      loadPromise = null;
    }
  })();

  await loadPromise;
}

export function useGitHubBookmarks(): UseGitHubBookmarksResult {
  onMounted(() => {
    if (!hasLoaded.value) {
      void loadBookmarks();
    }
  });

  return {
    bookmarks: readonly(bookmarks),
    isLoading: readonly(isLoading),
    error: readonly(error),
    refresh: loadBookmarks,
    replaceBookmarks,
    addBookmark: async (repo) => {
      if (bookmarks.value.some((bookmark) => bookmark.fullName === repo.fullName)) {
        return;
      }

      const response = await apiFetch(BOOKMARKS_API, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ repo: repo.fullName }),
      });
      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Failed to add bookmark."));
      }

      bookmarks.value = sortByName([...bookmarks.value, repo]);
      error.value = null;
    },
    removeBookmark: async (fullName) => {
      const [owner, ...repoParts] = fullName.split("/");
      const repo = repoParts.join("/");
      if (!owner || !repo) {
        throw new Error("Invalid bookmark name.");
      }

      const response = await apiFetch(`/api/integrations/github/bookmarks/${owner}/${repo}`, {
        method: "DELETE",
      });
      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Failed to remove bookmark."));
      }

      bookmarks.value = bookmarks.value.filter((bookmark) => bookmark.fullName !== fullName);
      error.value = null;
    },
    hasBookmark: (fullName) => bookmarks.value.some((bookmark) => bookmark.fullName === fullName),
  };
}
