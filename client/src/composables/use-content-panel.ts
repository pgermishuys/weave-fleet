/**
 * Provide/inject contract for the content panel (right panel).
 * Owns tab state, files explorer context, and changes drawer mode.
 */
import {
  type InjectionKey,
  type Ref,
  type ShallowRef,
  inject,
  provide,
  shallowRef,
  watch,
} from "vue";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type ContentPanelTab = "files" | "preview" | "details";
export type AllChangedFilter = "all" | "changed";
export type DrawerMode = "collapsed" | "expanded" | "maximized";

export interface FilesExplorerContext {
  allChangedFilter: AllChangedFilter;
  selectedFilePath: string | null;
  expandedDirs: Set<string>;
  searchQuery: string;
  scrollTop: number;
}

// ---------------------------------------------------------------------------
// Context shape
// ---------------------------------------------------------------------------

export interface ContentPanelContext {
  /**
   * Active tab in the content panel.
   */
  activeTab: Readonly<ShallowRef<ContentPanelTab>>;

  /**
   * Files explorer context (preserved when switching tabs).
   */
  filesContext: Readonly<ShallowRef<FilesExplorerContext>>;

  /**
   * Changes drawer mode (persisted to localStorage).
   */
  drawerMode: Readonly<ShallowRef<DrawerMode>>;

  /**
   * Select a file and switch to the preview tab.
   */
  selectFile: (path: string) => void;

  /**
   * Switch to the files tab (restores saved explorer context).
   */
  switchToFiles: () => void;

  /**
   * Switch to a specific tab.
   */
  switchToTab: (tab: ContentPanelTab) => void;

  /**
   * Set the changes drawer mode.
   */
  setDrawerMode: (mode: DrawerMode) => void;

  /**
   * Update files explorer context.
   */
  updateFilesContext: (patch: Partial<FilesExplorerContext>) => void;
}

// ---------------------------------------------------------------------------
// Injection key + helpers
// ---------------------------------------------------------------------------

export const ContentPanelContextKey: InjectionKey<ContentPanelContext> = Symbol("ContentPanelContext");

export function provideContentPanelContext(sessionId: Readonly<Ref<string | null>>): ContentPanelContext {
  const DRAWER_MODE_KEY = "weave:changes-drawer-mode";

  // State
  const activeTab = shallowRef<ContentPanelTab>("files");
  const filesContext = shallowRef<FilesExplorerContext>({
    allChangedFilter: "all",
    selectedFilePath: null,
    expandedDirs: new Set(),
    searchQuery: "",
    scrollTop: 0,
  });
  const drawerMode = shallowRef<DrawerMode>(readDrawerMode());

  // Persist drawer mode to localStorage
  watch(drawerMode, (mode) => {
    persistDrawerMode(mode);
  });

  // Reset transient state on session change
  watch(sessionId, (newId, oldId) => {
    if (newId !== oldId && newId !== null) {
      // Reset filter to "all", clear selection, collapse drawer, keep tab on "files"
      filesContext.value = {
        allChangedFilter: "all",
        selectedFilePath: null,
        expandedDirs: new Set(),
        searchQuery: "",
        scrollTop: 0,
      };
      drawerMode.value = "collapsed";
      activeTab.value = "files";
    }
  });

  // Actions
  function selectFile(path: string): void {
    filesContext.value = {
      ...filesContext.value,
      selectedFilePath: path,
    };
    activeTab.value = "preview";
  }

  function switchToFiles(): void {
    activeTab.value = "files";
  }

  function switchToTab(tab: ContentPanelTab): void {
    activeTab.value = tab;
  }

  function setDrawerMode(mode: DrawerMode): void {
    drawerMode.value = mode;
  }

  function updateFilesContext(patch: Partial<FilesExplorerContext>): void {
    const { expandedDirs, ...rest } = patch;
    filesContext.value = {
      ...filesContext.value,
      ...rest,
      // Preserve Set identity if not patched
      expandedDirs: expandedDirs ?? filesContext.value.expandedDirs,
    };
  }

  // Helper: read drawer mode from localStorage
  function readDrawerMode(): DrawerMode {
    if (typeof window === "undefined") {
      return "collapsed";
    }

    try {
      const raw = localStorage.getItem(DRAWER_MODE_KEY);
      if (raw === "expanded" || raw === "maximized" || raw === "collapsed") {
        return raw;
      }
      return "collapsed";
    } catch {
      return "collapsed";
    }
  }

  // Helper: persist drawer mode to localStorage
  function persistDrawerMode(mode: DrawerMode): void {
    if (typeof window === "undefined") {
      return;
    }

    try {
      localStorage.setItem(DRAWER_MODE_KEY, mode);
    } catch {
      // localStorage unavailable
    }
  }

  const ctx: ContentPanelContext = {
    activeTab,
    filesContext,
    drawerMode,
    selectFile,
    switchToFiles,
    switchToTab,
    setDrawerMode,
    updateFilesContext,
  };

  provide(ContentPanelContextKey, ctx);

  return ctx;
}

export function useContentPanelContext(): ContentPanelContext {
  const ctx = inject(ContentPanelContextKey);
  if (!ctx) {
    throw new Error(
      "useContentPanelContext() was called outside a component that provides ContentPanelContext. "
      + "Make sure a parent renders the content panel provider.",
    );
  }

  return ctx;
}
