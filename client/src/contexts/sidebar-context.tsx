
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { usePersistedState } from "@/hooks/use-persisted-state";
import { useIsMobileNav } from "@/hooks/use-media-query";
import { usePluginRuntime } from "@/plugins/context";
import { getSidebarPanels } from "@/plugins/slots";

const SIDEBAR_ACTIVE_VIEW_KEY = "weave:sidebar:activeView";
const SIDEBAR_WIDTH_KEY = "weave:sidebar:width";

export const SIDEBAR_MIN_WIDTH = 180;
export const SIDEBAR_MAX_WIDTH = 480;
export const SIDEBAR_RAIL_WIDTH = 56;
export const SIDEBAR_DEFAULT_WIDTH = 224;

// Kept for any consumers that imported SIDEBAR_COLLAPSED_WIDTH
// @deprecated use SIDEBAR_RAIL_WIDTH instead
export const SIDEBAR_COLLAPSED_WIDTH = SIDEBAR_RAIL_WIDTH;

export type SidebarView = string;

/** Views that show a contextual side panel */
const CORE_PANEL_VIEWS = new Set<SidebarView>(["fleet", "repositories"]);
const LEGACY_PANEL_VIEWS = new Set<SidebarView>(["fleet", "github", "repositories"]);

/** Whether the given view shows a contextual side panel */
export function viewHasPanel(
  view: SidebarView,
  panelViews: ReadonlySet<SidebarView> = LEGACY_PANEL_VIEWS
): boolean {
  return panelViews.has(view);
}

interface SidebarContextValue {
  /** Which view icon is active in the icon rail */
  activeView: SidebarView;
  /** Whether the contextual panel is visible (derived from activeView) */
  readonly panelOpen: boolean;
  /** Set the active view — panel visibility is derived automatically */
  setActiveView: (view: SidebarView) => void;
  /** Views that own a contextual side panel */
  readonly panelViews: ReadonlySet<SidebarView>;
  /** Toggle sidebar panel (⌘B): switches between welcome and last panel view */
  toggleSidebar: () => void;
  /** Read-only backwards-compat alias: collapsed === !panelOpen */
  readonly collapsed: boolean;
  width: number;
  setWidth: (value: number | ((prev: number) => number)) => void;
  isResizing: boolean;
  setIsResizing: (value: boolean) => void;
  /** True when in mobile nav mode (< 717px fold breakpoint) — sidebar renders as a Sheet drawer */
  isMobileNav: boolean;
  /** Whether the mobile sidebar drawer is open */
  mobileDrawerOpen: boolean;
  /** Open or close the mobile sidebar drawer */
  setMobileDrawerOpen: (open: boolean) => void;
}

const SidebarContext = createContext<SidebarContextValue | null>(null);

// ── Synchronous localStorage migration ───────────────────────────────────────
// Cleans up legacy keys from previous sidebar implementations.
let _migrated = false;

function migrateSidebarStorage(): void {
  if (_migrated) return;
  _migrated = true;

  try {
    // Remove legacy keys that are no longer used
    localStorage.removeItem("weave:sidebar:collapsed");
    localStorage.removeItem("weave:sidebar:panelOpen");

    // Migrate old activeView values that no longer exist
    const raw = localStorage.getItem(SIDEBAR_ACTIVE_VIEW_KEY);
    if (raw !== null) {
      try {
        JSON.parse(raw);
      } catch {
        localStorage.removeItem(SIDEBAR_ACTIVE_VIEW_KEY);
      }
    }
  } catch {
    // localStorage unavailable (SSR / private browsing)
  }
}

interface SidebarProviderProps {
  children: ReactNode;
}

export function SidebarProvider({ children }: SidebarProviderProps) {
  // Run synchronous migration before any hook reads localStorage
  migrateSidebarStorage();
  const { manifests } = usePluginRuntime();
  const pluginPanels = useMemo(() => getSidebarPanels(manifests), [manifests]);
  const panelViews = useMemo(
    () => new Set<SidebarView>([...LEGACY_PANEL_VIEWS, ...CORE_PANEL_VIEWS, ...pluginPanels.map((panel) => panel.viewId)]),
    [pluginPanels]
  );

  const [activeView, setActiveViewState] = usePersistedState<SidebarView>(
    SIDEBAR_ACTIVE_VIEW_KEY,
    "fleet"
  );

  const [width, setWidth] = usePersistedState<number>(
    SIDEBAR_WIDTH_KEY,
    SIDEBAR_DEFAULT_WIDTH
  );

  const [isResizing, setIsResizing] = useState(false);
  const [mobileDrawerOpen, setMobileDrawerOpen] = useState(false);

  // Derive mobile mode from viewport width (< fold breakpoint = 717px)
  const isMobileNav = useIsMobileNav();

  // Track the last panel view so ⌘B can restore it
  const lastPanelViewRef = useRef<SidebarView>("fleet");

  useEffect(() => {
    if (viewHasPanel(activeView, panelViews)) {
      lastPanelViewRef.current = activeView;
    }
  }, [activeView, panelViews]);

  // Derive panel visibility from the active view
  const panelOpen = viewHasPanel(activeView, panelViews);

  const setActiveView = useCallback(
    (view: SidebarView) => {
      if (viewHasPanel(view, panelViews)) {
        lastPanelViewRef.current = view;
      }
      setActiveViewState(view);
    },
    [panelViews, setActiveViewState]
  );

  const toggleSidebar = useCallback(() => {
    if (isMobileNav) {
      // On mobile, ⌘B opens/closes the drawer
      setMobileDrawerOpen((open) => !open);
      return;
    }

    if (!viewHasPanel(activeView, panelViews)) {
      // On a non-panel view (e.g. welcome), always restore the last panel view
      // and ensure the sidebar panel becomes visible.
      setActiveViewState(lastPanelViewRef.current);
      return;
    }

    // Panel is showing → switch to welcome (hides panel)
    setActiveViewState("welcome");
  }, [activeView, isMobileNav, panelViews, setActiveViewState]);

  return (
    <SidebarContext.Provider
      value={{
        activeView,
        panelOpen,
        setActiveView,
        panelViews,
        toggleSidebar,
        get collapsed() {
          return !panelOpen;
        },
        width,
        setWidth,
        isResizing,
        setIsResizing,
        isMobileNav,
        mobileDrawerOpen,
        setMobileDrawerOpen,
      }}
    >
      {children}
    </SidebarContext.Provider>
  );
}

export function useSidebar(): SidebarContextValue {
  const ctx = useContext(SidebarContext);
  if (!ctx) {
    throw new Error("useSidebar must be used within a SidebarProvider");
  }
  return ctx;
}
