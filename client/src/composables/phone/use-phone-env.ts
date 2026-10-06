import { onUnmounted, shallowRef, watch } from "vue";
import { useThemeStore } from "@/stores/theme";
import { RETIRED_LOOK_KEY, isIosWebKit, readPlatformSignals } from "@/lib/phone/platform";

/**
 * Fleet's phone layer, switched on while a phone page is on screen (PhoneShell calls this once):
 * - `data-phone` and light/dark as attributes on <html>, which scope phone.css (one Fleet design on every phone);
 * - the phone's own type scale instead of the desktop's font size setting;
 * - the status bar colour (`theme-color`) matching the window chrome the panels float on;
 * - the keyboard: the app frame follows the visual viewport (--ph-vvh, --ph-vvtop) and --ph-kb is its height;
 * - instant pressed states on buttons and rows, as native controls have.
 * Everything is undone when the phone pages go away.
 */

/** The keyboard's height over the page, in px. */
export const keyboardHeight = shallowRef(0);

const signals = typeof navigator === "undefined" ? { userAgent: "" } : readPlatformSignals();
export const iosWebKit = isIosWebKit(signals);
export const standalone = typeof window !== "undefined"
  && (window.matchMedia?.("(display-mode: standalone)").matches || (navigator as Navigator & { standalone?: boolean }).standalone === true);

let lastEdgeTouch = 0;
/** Safari's own edge swipe went back just now (it animates that itself, so the page shouldn't again). */
export function safariSwipedBack(): boolean {
  return iosWebKit && !standalone && performance.now() - lastEdgeTouch < 1200;
}

const PRESSABLE = ".ph-btn,.ph-icon-btn,.ph-tab,.ph-srow,.ph-set:not(.ph-set--static),.ph-tool,.ph-choice,.ph-mi,.ph-chip,.ph-sel,.ph-send,.ph-link,.ph-mhead,.ph-pcard__open,.ph-press";
const DELAYED = ".ph-srow,.ph-set,.ph-tool,.ph-choice,.ph-mhead,.ph-pcard__open";

/** Pressed states: instant on buttons, a beat later on rows (so a scroll doesn't flash them). Port of kit.js. */
function installPress(): () => void {
  let current: HTMLElement | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let startX = 0;
  let startY = 0;
  let addedAt = 0;

  const add = (el: HTMLElement): void => {
    el.classList.add("is-pressed");
    addedAt = performance.now();
  };
  const release = (quick: boolean): void => {
    clearTimeout(timer);
    const el = current;
    current = null;
    if (!el) return;
    if (quick && !el.classList.contains("is-pressed")) add(el);
    const wait = Math.max(0, 110 - (performance.now() - addedAt));
    setTimeout(() => el.classList.remove("is-pressed"), wait);
  };
  const start = (target: EventTarget | null, x: number, y: number): void => {
    const el = (target as Element | null)?.closest?.<HTMLElement>(PRESSABLE);
    if (!el || (el as HTMLButtonElement).disabled) return;
    current = el;
    startX = x;
    startY = y;
    if (el.matches(DELAYED)) timer = setTimeout(() => current === el && add(el), 70);
    else add(el);
  };
  const cancel = (): void => {
    clearTimeout(timer);
    const el = current;
    current = null;
    el?.classList.remove("is-pressed");
  };

  const onTouchStart = (event: TouchEvent): void => {
    const touch = event.touches[0];
    if (touch.clientX < 24 || touch.clientX > window.innerWidth - 24) lastEdgeTouch = performance.now();
    start(event.target, touch.clientX, touch.clientY);
  };
  const onTouchMove = (event: TouchEvent): void => {
    if (!current) return;
    const touch = event.touches[0];
    if (Math.hypot(touch.clientX - startX, touch.clientY - startY) > 10) cancel();
  };
  const onTouchEnd = (): void => release(true);
  const onTouchCancel = (): void => release(false);
  const onPointerDown = (event: PointerEvent): void => {
    if (event.pointerType === "mouse") start(event.target, event.clientX, event.clientY);
  };
  const onPointerUp = (event: PointerEvent): void => {
    if (event.pointerType === "mouse") release(true);
  };

  document.addEventListener("touchstart", onTouchStart, { passive: true });
  document.addEventListener("touchmove", onTouchMove, { passive: true });
  document.addEventListener("touchend", onTouchEnd, { passive: true });
  document.addEventListener("touchcancel", onTouchCancel, { passive: true });
  document.addEventListener("pointerdown", onPointerDown);
  document.addEventListener("pointerup", onPointerUp);
  return () => {
    document.removeEventListener("touchstart", onTouchStart);
    document.removeEventListener("touchmove", onTouchMove);
    document.removeEventListener("touchend", onTouchEnd);
    document.removeEventListener("touchcancel", onTouchCancel);
    document.removeEventListener("pointerdown", onPointerDown);
    document.removeEventListener("pointerup", onPointerUp);
  };
}

/** The app frame follows the visual viewport, so content and buttons stay above the iOS keyboard. */
function installViewport(root: HTMLElement): () => void {
  const viewport = window.visualViewport;
  const update = (): void => {
    const height = viewport ? viewport.height : window.innerHeight;
    const top = viewport ? viewport.offsetTop : 0;
    const keyboard = Math.max(0, window.innerHeight - height);
    root.style.setProperty("--ph-vvh", `${height}px`);
    root.style.setProperty("--ph-vvtop", `${top}px`);
    root.style.setProperty("--ph-kb", `${keyboard}px`);
    root.classList.toggle("ph-kb-open", keyboard > 80);
    keyboardHeight.value = keyboard;
    // The layout can't scroll the page itself; undo any page scroll iOS makes to show a field.
    if (window.scrollY) window.scrollTo(0, 0);
  };
  // Focusing a field scrolls even overflow-hidden frames to show it, which would shift the whole app: put them back.
  const FRAMES = ".ph-app,.ph-stage,.ph-overlays,.ph-screen,.ph-sheet-layer,.ph-sheet,.ph-sheet-pages,.ph-sheet-page";
  const onScroll = (event: Event): void => {
    const target = event.target;
    if (target instanceof HTMLElement && target.matches(FRAMES) && (target.scrollTop || target.scrollLeft)) {
      target.scrollTop = 0;
      target.scrollLeft = 0;
    }
  };
  document.addEventListener("scroll", onScroll, true);
  viewport?.addEventListener("resize", update);
  viewport?.addEventListener("scroll", update);
  window.addEventListener("resize", update);
  update();
  return () => {
    document.removeEventListener("scroll", onScroll, true);
    viewport?.removeEventListener("resize", update);
    viewport?.removeEventListener("scroll", update);
    window.removeEventListener("resize", update);
    for (const name of ["--ph-vvh", "--ph-vvtop", "--ph-kb"]) root.style.removeProperty(name);
    root.classList.remove("ph-kb-open");
  };
}

export function usePhoneEnv() {
  const root = document.documentElement;
  const theme = useThemeStore();
  const scheme = shallowRef<"light" | "dark">(theme.resolvedTheme.colorScheme);
  watch(() => theme.resolvedTheme.colorScheme, (next) => {
    scheme.value = next;
  });
  // "System" follows the phone's dark mode; the store repaints <html> but its computed doesn't change, so look there.
  const media = window.matchMedia?.("(prefers-color-scheme: dark)");
  const onSystemScheme = (): void => {
    requestAnimationFrame(() => {
      scheme.value = root.style.colorScheme === "light" ? "light" : "dark";
    });
  };
  media?.addEventListener?.("change", onSystemScheme);

  root.dataset.phone = "";
  try {
    localStorage.removeItem(RETIRED_LOOK_KEY);
  } catch {
    // no storage: nothing was kept
  }
  root.dataset.phoneScheme = scheme.value;
  if (standalone) root.classList.add("ph-standalone");
  // The phone uses its own type scale, not the desktop's font size setting.
  const desktopFontSize = root.style.fontSize;
  root.style.removeProperty("font-size");
  // index.html painted the background before the app loaded; from here phone.css paints it.
  root.style.removeProperty("background");

  // The status bar is the window chrome the panels float on.
  function paintStatusBar(): void {
    const chrome = getComputedStyle(root).getPropertyValue("--main-bg").trim() || (scheme.value === "light" ? "#F3F2EF" : "#0d0d10");
    for (const meta of document.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]')) meta.content = chrome;
  }

  watch(scheme, (next) => {
    root.dataset.phoneScheme = next;
    paintStatusBar();
  });
  watch(() => theme.resolvedThemeId, () => requestAnimationFrame(paintStatusBar));
  paintStatusBar();

  const removePress = installPress();
  const removeViewport = installViewport(root);

  onUnmounted(() => {
    media?.removeEventListener?.("change", onSystemScheme);
    removePress();
    removeViewport();
    delete root.dataset.phone;
    delete root.dataset.phoneScheme;
    root.classList.remove("ph-standalone");
    if (desktopFontSize) root.style.fontSize = desktopFontSize;
  });

  return { scheme };
}
