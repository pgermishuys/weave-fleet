// When the phone last opened each session's Pages, in the phone's Keychain/Keystore-backed storage (as the machines are).
import { useSyncExternalStore } from "react";
import { storage } from "~/fleet/machines";
import { PagesSeenStore, newPagesOf } from "~/fleet/pages-seen-core";

export const pagesSeen = new PagesSeenStore(storage);
void pagesSeen.load();

/** Re-renders when a session's seen time is read or marked; read the values with `pagesSeen.get` and {@link newPagesOf}. */
export const usePagesSeen = () => useSyncExternalStore(pagesSeen.subscribe, pagesSeen.snapshot);

export { newPagesOf };
