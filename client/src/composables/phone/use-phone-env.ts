import { onUnmounted, shallowRef, watch } from "vue";
import { useThemeStore } from "@/stores/theme";
import { LOOK_STORAGE_KEY, isIosWebKit, readLookSignals, resolveLook, type PhoneLook } from "@/lib/phone/look";

/**
 * The phone's native layer, switched on while a phone page is on screen (PhoneShell calls this once):
 * - the look (iOS or Android) and light/dark as attributes on <html>, which scope phone.css;
 * - the phone's own text size (17px body, the iPhone's text size setting) instead of the desktop's;
 * - the status bar colour (`theme-color`) matching the page, black while a sheet pushes the page back;
 * - the keyboard: the app frame follows the visual viewport (--ph-vvh, --ph-vvtop) and --ph-kb is its height;
 * - instant pressed states on buttons and rows (with a ripple on Android), as native controls have.
 * Everything is undone when the phone pages go away.
 */

/** The look in use, for components that draw differently on each (back arrow, tab bar, New session button). */
export const phoneLook = shallowRef<PhoneLook>("ios");
/** How far a sheet has pushed the page back (0–1): the status bar goes black with it. */
export const pageRecessed = shallowRef(0);
/** The keyboard's height over the page, in px. */
export const keyboardHeight = shallowRef(0);

const signals = typeof navigator === "undefined" ? { userAgent: "" } : readLookSignals();
export const iosWebKit = isIosWebKit(signals);
export const standalone = typeof window !== "undefined"
  && (window.matchMedia?.("(display-mode: standalone)").matches || (navigator as Navigator & { standalone?: boolean }).standalone === true);

let lastEdgeTouch = 0;
/** Safari's own edge swipe went back just now (it animates that itself, so the page shouldn't again). */
export function safariSwipedBack(): boolean {
  return iosWebKit && !standalone && performance.now() - lastEdgeTouch < 1200;
}

const PRESSABLE = ".ph-btn,.ph-navbtn,.ph-tab,.ph-row:not(.ph-row--static),.ph-step,.ph-send,.ph-composer__plus,.ph-link-btn,.ph-ask__open,.ph-fab,.ph-press";
const DELAYED = ".ph-row,.ph-step,.ph-ask__open";

function readLook(): PhoneLook {
  let stored: string | null = null;
  try {
    stored = localStorage.getItem(LOOK_STORAGE_KEY);
  } catch {
    // no storage: detect every time
  }
  const query = new URLSearchParams(window.location.search).get("look");
  const resolved = resolveLook({ query, stored, signals });
  try {
    if (resolved.store === null) localStorage.removeItem(LOOK_STORAGE_KEY);
    else if (resolved.store) localStorage.setItem(LOOK_STORAGE_KEY, resolved.store);
  } catch {
    // no storage: the override lasts this page
  }
  return resolved.look;
}

/** Pressed states: instant on buttons, a beat later on rows (so a scroll doesn't flash them). Port of kit.js. */
function installPress(root: HTMLElement): () => void {
  let current: HTMLElement | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let startX = 0;
  let startY = 0;
  let addedAt = 0;
  let ripple: HTMLElement | null = null;

  const add = (el: HTMLElement, x: number, y: number): void => {
    el.classList.add("is-pressed");
    addedAt = performance.now();
    if (root.dataset.phone !== "android") return;
    const style = getComputedStyle(el);
    if (style.position === "static") return;
    const rect = el.getBoundingClientRect();
    const size = Math.hypot(Math.max(x - rect.left, rect.right - x), Math.max(y - rect.top, rect.bottom - y)) * 2;
    ripple = document.createElement("span");
    ripple.className = "ph-ripple";
    Object.assign(ripple.style, { width: `${size}px`, height: `${size}px`, left: `${x - rect.left - size / 2}px`, top: `${y - rect.top - size / 2}px` });
    if (style.overflow === "visible") el.style.overflow = "hidden";
    el.appendChild(ripple);
  };
  const release = (quick: boolean): void => {
    clearTimeout(timer);
    const el = current;
    current = null;
    if (!el) return;
    if (quick && !el.classList.contains("is-pressed")) add(el, startX, startY);
    const done = ripple;
    ripple = null;
    const wait = Math.max(0, 110 - (performance.now() - addedAt));
    setTimeout(() => {
      el.classList.remove("is-pressed");
      if (done) {
        done.classList.add("ph-ripple--out");
        setTimeout(() => done.remove(), 500);
      }
    }, wait);
  };
  const start = (target: EventTarget | null, x: number, y: number): void => {
    const el = (target as Element | null)?.closest?.<HTMLElement>(PRESSABLE);
    if (!el || (el as HTMLButtonElement).disabled) return;
    current = el;
    startX = x;
    startY = y;
    if (el.matches(DELAYED)) timer = setTimeout(() => current === el && add(el, x, y), 70);
    else add(el, x, y);
  };
  const cancel = (): void => {
    clearTimeout(timer);
    const el = current;
    current = null;
    el?.classList.remove("is-pressed");
    ripple?.remove();
    ripple = null;
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

  phoneLook.value = readLook();
  root.dataset.phone = phoneLook.value;
  root.dataset.phoneScheme = scheme.value;
  if (standalone) root.classList.add("ph-standalone");
  // The phone uses its own text size, not the desktop's setting.
  const desktopFontSize = root.style.fontSize;
  root.style.removeProperty("font-size");
  // index.html painted the background before the app loaded; from here phone.css paints it.
  root.style.removeProperty("background");

  function paintStatusBar(): void {
    const background = getComputedStyle(root).getPropertyValue("--main-bg").trim() || (scheme.value === "light" ? "#F3F2EF" : "#0d0d10");
    const color = pageRecessed.value > 0.5 ? "#000000" : background;
    for (const meta of document.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]')) meta.content = color;
  }

  watch(scheme, (next) => {
    root.dataset.phoneScheme = next;
    paintStatusBar();
  });
  watch(() => theme.resolvedThemeId, () => requestAnimationFrame(paintStatusBar));
  watch(pageRecessed, paintStatusBar);
  paintStatusBar();

  const removePress = installPress(root);
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

  return { look: phoneLook, scheme };
}
