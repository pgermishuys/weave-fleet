/**
 * Fleet mods, Stage 1: the types a mod and the mod host are checked against.
 *
 * The prose is docs/mods/api.md; where the two disagree, this file wins and the doc is the bug. Names follow Claude
 * Code's mods API (https://code.claude.com/docs/en/plugins/mods/reference) wherever the concept is the same, so a mod
 * written for one reads naturally in the other. Everything here is the Stage 1 subset: no `$.fs`, `$.process`,
 * `$.http` or `$.model`, no prompt, tool or permission events.
 *
 * A mod imports types only:
 *
 * @example
 * import type { Register } from "fleet-mods";
 *
 * export const register: Register = (on) => {
 *   on("ui.render", { component: "ToolUse", props: { tool: "bash" } }, async ($, e, next) => {
 *     const { Pill } = $.ui.resolve(e);
 *     return /\bdotnet test\b/.test(String(e.props.input.command ?? "")) ? Pill({ tone: "neutral", label: "tests" }) : next(e);
 *   });
 * };
 */
declare module "fleet-mods" {
  // ─── The protocol version this file describes ────────────────────────────────────────────────────────────────────

  /** Bumped when Fleet and the host stop understanding each other. The host refuses any other value. */
  export const PROTOCOL: 1;

  // ─── Registering hooks ───────────────────────────────────────────────────────────────────────────────────────────

  /** What the hooks module exports. Called once each time the module loads (Keep, reload, host start). */
  export type Register = (on: On, options: ModOptions) => void;

  /** Values from the manifest's `options` (none in Stage 1, so always `{}`). Kept for parity with Claude Code. */
  export type ModOptions = Readonly<Record<string, never>>;

  /**
   * Registers a hook. Call it only from `register`, synchronously, with a string literal event name and, if given, an
   * object-literal matcher: the static check reads both from the source.
   *
   * Hooks of one module run in the order `on` was called. Registering the same event twice without a matcher is an
   * error that refuses the module.
   */
  export interface On {
    <N extends EventName>(event: N, hook: Hook<N>): Registration<N>;
    <N extends EventName>(event: N, matcher: Matcher<N>, hook: Hook<N>): Registration<N>;
  }

  export interface Registration<N extends EventName> {
    /**
     * Sets the handler run when this hook throws, outruns its budget or returns what the event doesn't take. Its
     * answer, within 1 s of its own time, stands as the hook's result. A failure a handler answers is not a strike.
     */
    catch(handler: CatchHandler<N>): void;
  }

  /** One hook: `($, e, next)`. Observe and return `next(e)`, rewrite with `next({ ...e, … })`, or answer without `next`. */
  export type Hook<N extends EventName> = ($: Fleet, e: Frozen<EventArgs[N]>, next: Next<N>) => Promise<EventResult[N]> | EventResult[N];

  export type CatchHandler<N extends EventName> = ($: Fleet, e: Frozen<EventArgs[N]>, next: CatchNext<N>) => Promise<EventResult[N]> | EventResult[N];

  export interface Next<N extends EventName> {
    /** Runs the rest of the chain (later mods, then Fleet's own behaviour) and resolves to its result. */
    (e: EventArgs[N]): Promise<EventResult[N]>;
    /** Aborts when the dispatch is abandoned: the hook ran out of budget, or the session went away. */
    readonly signal: AbortSignal;
    /** The hook's time limit. Its own time only: waits inside `next` and `$` calls don't count. */
    readonly budget: { readonly ms: number; readonly remainingMs: number };
    /** The event's name, for a hook registered on more than one. */
    readonly event: N;
  }

  export interface CatchNext<N extends EventName> extends Next<N> {
    /** Why the hook failed. */
    readonly error: HookFailure;
    /** True when the hook had called `next` before it failed; `next(e)` then resolves to that result again. */
    readonly called: boolean;
  }

  export interface HookFailure {
    /** `throw`: threw, or returned what the event doesn't take (an invalid tree). `timeout`: outran its budget. */
    readonly kind: "throw" | "timeout";
    readonly message?: string;
  }

  /**
   * A filter on the event's fields: the hook runs only when every field matches. A value matches itself, an array
   * matches any of its values, a RegExp matches strings by pattern, and an object matches a nested field the same way.
   * For a tool row: `{ component: "ToolUse", props: { tool: "bash" } }`.
   */
  export type Matcher<N extends EventName> = N extends "ui.render" ? RenderMatcher : FieldMatcher<EventArgs[N]>;

  type MatchValue<V> = V extends string ? V | RegExp | readonly (V | RegExp)[] : V extends number | boolean ? V | readonly V[] : never;
  type FieldMatcher<T> = { readonly [K in keyof T]?: MatchValue<T[K]> };

  export type RenderMatcher =
    | {
        readonly component: "ToolUse" | "ToolResult" | readonly ("ToolUse" | "ToolResult")[];
        readonly props?: FieldMatcher<Pick<ToolRowProps, "tool" | "rawTool" | "category" | "status">>;
      }
    | { readonly component: RenderComponent | readonly RenderComponent[] };

  // ─── Events ──────────────────────────────────────────────────────────────────────────────────────────────────────

  export type EventName = keyof EventArgs;

  export interface EventArgs {
    "session.start": SessionStartEvent;
    "turn.complete": TurnCompleteEvent;
    "ui.render": UiRenderEvent;
    "ui.press": UiPressEvent;
    "ui.input": UiInputEvent;
    "ui.select": UiSelectEvent;
  }

  export interface EventResult {
    "session.start": SessionStartEvent;
    "turn.complete": TurnCompleteEvent;
    "ui.render": Element | null;
    "ui.press": UiPressResult;
    "ui.input": UiInputResult;
    "ui.select": UiSelectResult;
  }

  /**
   * Once per mod per session, before the mod's first other event for that session, and again after the mod reloads
   * (a draft saved, a new version kept, the host restarted). Start timers and load from `$.store` here. Watch only.
   */
  export interface SessionStartEvent {
    readonly sessionId: string;
    /** `start`: first time this module sees the session. `reload`: the module, or the host, started again. */
    readonly reason: "start" | "reload";
  }

  /** A turn ended, including one the user stopped (`isAborted`) or one that failed (`isFailed`). Watch only. */
  export interface TurnCompleteEvent {
    readonly sessionId: string;
    /** The turn's assistant message id. */
    readonly turnId: string;
    readonly isAborted: boolean;
    readonly isFailed: boolean;
    /** What the failure card says, when `isFailed`. */
    readonly failure?: string;
    readonly agent?: string;
    /** `provider/model`, when the harness reported it. */
    readonly model?: string;
    readonly usage?: { readonly input: number; readonly output: number; readonly cacheRead: number; readonly cacheWrite: number };
    /** In the provider's currency unit, as Fleet's cost column shows it. */
    readonly cost?: number;
  }

  /** The places a mod can draw in Stage 1. */
  export type RenderComponent = "ToolUse" | "ToolResult" | "ComposerBand" | "StatusChip" | "Pane";

  /** Fleet fires `ui.render` when it is about to draw a site the mod hooked. Return a tree, `next(e)`, or `null`. */
  export type UiRenderEvent =
    | RenderEventOf<"ToolUse", ToolRowProps>
    | RenderEventOf<"ToolResult", ToolRowProps>
    | RenderEventOf<"ComposerBand", ComposerBandProps>
    | RenderEventOf<"StatusChip", StatusChipProps>
    | RenderEventOf<"Pane", PaneProps>;

  interface RenderEventOf<C extends RenderComponent, P> {
    readonly component: C;
    readonly sessionId: string;
    /**
     * Which instance of the site: the tool call id for `ToolUse`/`ToolResult`, the pane's id for `Pane`, the session id
     * for `ComposerBand` and `StatusChip`.
     */
    readonly requestId: string;
    readonly props: P;
  }

  /**
   * One tool call's row. `ToolUse` draws on the row's line, after Fleet's label; `ToolResult` draws the body the row
   * shows when it is opened, in place of Fleet's. Both are rendered when the row is first shown, when the call's status
   * changes and when the mod invalidates, never on streamed output.
   */
  export interface ToolRowProps {
    /** The canonical name from Fleet's tool registry, the same whichever harness made the call: `bash`, `read`, `edit`… */
    readonly tool: string;
    /** The name as the harness sent it (`Bash`, `mcp__fleet__fleet_page_show`). */
    readonly rawTool: string;
    readonly category: ToolCategory;
    readonly status: "pending" | "running" | "completed" | "error" | "cancelled";
    /** The row's title as Fleet shows it. */
    readonly title: string;
    /** The call's arguments, as the harness sent them (at most 64 KiB of JSON; larger is `{}` with `inputTruncated`). */
    readonly input: Readonly<Record<string, unknown>>;
    readonly inputTruncated: boolean;
    /** The call's output text, or the error's. The last 256 KiB when longer, with `outputTruncated`. */
    readonly output: string;
    readonly outputTruncated: boolean;
    readonly durationMs?: number;
  }

  export type ToolCategory = "read" | "search" | "edit" | "shell" | "web" | "subagent" | "question" | "plan" | "skill" | "fleet" | "other";

  /** The band above the composer, desktop and phone. Shared by every mod: a tree replaces what later mods draw. */
  export interface ComposerBandProps {
    /** The session is running a turn. */
    readonly isWorking: boolean;
  }

  /** A chip at the end of the status bar, for the session on screen. Inline elements only. */
  export type StatusChipProps = Readonly<Record<string, never>>;

  /** A pane the mod opened with `$.ui.open`, in the session's content panel (a sheet on the phone). */
  export interface PaneProps {
    readonly title: string;
  }

  /** A `Button` the mod drew was pressed. Hooks run first; the end of the chain runs the button's `onPress`. */
  export interface UiPressEvent extends ControlEvent {}
  export interface UiPressResult {
    readonly element: string;
  }

  /** An `Input` changed (`change`, at most 4 a second) or was submitted (`submit`). The chain ends in its callback. */
  export interface UiInputEvent extends ControlEvent {
    readonly kind: "change" | "submit";
    readonly value: string;
  }
  export interface UiInputResult {
    readonly element: string;
    readonly value: string;
  }

  /** A `Select` changed. The chain ends in its `onSelect`. */
  export interface UiSelectEvent extends ControlEvent {
    readonly value: string;
  }
  export interface UiSelectResult {
    readonly element: string;
    readonly value: string;
  }

  interface ControlEvent {
    readonly sessionId: string;
    /** The name of the mod that drew the control. The host fills it (and `element`) from the control's handle. */
    readonly mod: string;
    /** The control's `key`. */
    readonly element: string;
    readonly component: RenderComponent;
    readonly requestId: string;
    /** Where the user was: `desktop` (a browser or the desktop app) or `phone`. */
    readonly surface: Surface;
  }

  export type Surface = "desktop" | "phone";

  // ─── $, the only way out ─────────────────────────────────────────────────────────────────────────────────────────

  /**
   * Everything a mod can do outside its own code. Write each call in full, `$.ns.method(…)`: the static check lists
   * them, and a module that reaches `$` any other way (aliasing it, `$[name]`, destructuring) doesn't load. `$` may be
   * passed to a function whose parameter is also named `$`.
   */
  export interface Fleet {
    readonly mod: { readonly name: string; readonly version: number | "draft" };
    readonly ui: UiApi;
    readonly state: StateApi;
    readonly store: StoreApi;
    readonly session: SessionApi;
    readonly clock: ClockApi;
  }

  export interface UiApi {
    /** The element factories. One set serves desktop and phone; Fleet lays the tree out for each. */
    resolve(e: UiRenderEvent): Elements;
    /** Draw this mod's sites again for the current session (all sessions outside a session's events). Throttled to 10 a second. */
    invalidate(event: "ui.render"): void;
    /** Opens (or focuses) a pane in the current session. `id`: letters, digits, `_`, `-`, up to 64. */
    open(pane: { id: string; title?: string }): Promise<void>;
    close(pane: { id: string }): Promise<void>;
    /** A Fleet notice on the screens showing the current session, titled with the mod's name. 500 characters at most. */
    toast(text: string, options?: { timeoutMs?: number; tone?: "accent" | "warn" }): void;
    /** A line in the mod's log: Settings → Mods shows it, and `fleet_mod_check`/`fleet_mod_test` return it to the agent. */
    log(text: string, options?: { level?: "info" | "warn" | "error" }): void;
  }

  /**
   * Reactive values for one mod in one session, kept by the host until the session is archived or the host stops. A
   * `ui.render` hook that reads a key redraws when the key is written; it may not write. Keys are string literals.
   */
  export interface StateApi {
    get<T extends Json = Json>(key: string): T | undefined;
    set(key: string, value: Json): void;
  }

  /** Per user, per mod (a draft shares its kept mod's store), saved by Fleet. 4 MiB of JSON in total. */
  export interface StoreApi {
    get<T extends Json = Json>(key: string): Promise<T | undefined>;
    set(key: string, value: Json): Promise<void>;
    delete(key: string): Promise<void>;
    keys(): Promise<string[]>;
  }

  /** The session the current event belongs to. Outside a session's events (none in Stage 1) these reject. */
  export interface SessionApi {
    id(): Promise<string>;
    title(): Promise<string>;
    /** `opencode`, `opencode2`, `claude-code`, `pi`. */
    harness(): Promise<string>;
    /** The session's working folder on Fleet's machine. A string; there is no `$.fs` in Stage 1. */
    cwd(): Promise<string>;
    /** Where the session is open right now. Empty when nobody is looking. */
    surfaces(): Promise<Surface[]>;
  }

  /** Timers belong to the mod and the session they were started in, and stop when the module reloads or the session goes. */
  export interface ClockApi {
    now(): number;
    /** Runs `fn` once after `ms`. */
    after(ms: number, fn: () => void | Promise<void>): Timer;
    /** Runs `fn` every `ms`, at least 100. Twenty timers per mod per session at most. */
    every(ms: number, fn: () => void | Promise<void>): Timer;
  }

  export interface Timer {
    cancel(): void;
  }

  export type Json = string | number | boolean | null | readonly Json[] | { readonly [key: string]: Json };

  // ─── Elements ────────────────────────────────────────────────────────────────────────────────────────────────────

  /**
   * A tree is data. Fleet draws it with its own components, in its theme, on desktop and phone. Callbacks never cross
   * to Fleet: the host keeps them and sends a handle. A prop or element not listed here makes the whole tree invalid,
   * and Fleet draws the site as if the mod weren't there (and counts a failure).
   */
  export interface Elements {
    Box(props: BoxProps): Element;
    Text(props: TextProps): Element;
    Pill(props: PillProps): Element;
    Icon(props: IconProps): Element;
    Button(props: ButtonProps): Element;
    Input(props: InputProps): Element;
    Select(props: SelectProps): Element;
    Markdown(props: MarkdownProps): Element;
    Code(props: CodeProps): Element;
    Page(props: PageProps): Element;
  }

  /**
   * What a factory returns, and what `next(e)` resolves to at a site. `{ type: "Fleet" }` is Fleet's own drawing of the
   * site (nothing at `ToolUse`, `ComposerBand` and `StatusChip`; Fleet's body at `ToolResult`). Put it in a `Box` to
   * keep it beside yours; at most once per tree.
   */
  export type Element =
    | { readonly type: "Box"; readonly props: Omit<BoxProps, "children">; readonly children: readonly Child[] }
    | { readonly type: "Text"; readonly props: Omit<TextProps, "children">; readonly children: readonly Child[] }
    | { readonly type: "Pill" | "Icon" | "Button" | "Input" | "Select" | "Markdown" | "Code" | "Page"; readonly props: Readonly<Record<string, unknown>> }
    | { readonly type: "Fleet" };

  export type Child = Element | string | number | false | null | undefined;

  /** Colours by role only, so trees follow the theme. */
  export type ColorRole = "text" | "muted" | "accent" | "good" | "warn" | "bad";
  export type Tone = "good" | "warn" | "bad" | "neutral" | "accent";
  /** Spacing in Fleet's steps: 1 is 4 px. */
  export type Space = 0 | 1 | 2 | 3 | 4 | 6 | 8;

  export interface BoxProps {
    key?: string;
    /** `row` when absent. */
    flexDirection?: "row" | "column";
    gap?: Space;
    padding?: Space;
    paddingX?: Space;
    paddingY?: Space;
    alignItems?: "start" | "center" | "end" | "stretch" | "baseline";
    justifyContent?: "start" | "center" | "end" | "space-between";
    flexWrap?: "wrap" | "nowrap";
    flexGrow?: 0 | 1;
    /** A share of the parent's width. Never a fixed size, so trees fit the phone. */
    width?: `${number}%`;
    /** `round` and `single` draw Fleet's card border; `quote` a bar down the left. */
    borderStyle?: "round" | "single" | "dashed" | "quote";
    borderColor?: ColorRole;
    /** `subtle`: Fleet's raised surface. `tint`: the border colour's wash. */
    background?: "subtle" | "tint";
    children?: Child | readonly Child[];
  }

  export interface TextProps {
    color?: ColorRole;
    bold?: boolean;
    italic?: boolean;
    strikethrough?: boolean;
    /** Monospace. */
    code?: boolean;
    /** Shorthand for `color: "muted"`. */
    dimColor?: boolean;
    wrap?: "wrap" | "truncate";
    children?: Child | readonly Child[];
  }

  export interface PillProps {
    tone: Tone;
    label: string;
    icon?: IconName;
  }

  export interface IconProps {
    name: IconName;
    color?: ColorRole;
    /** Read out to screen readers; without it the icon is decoration. */
    label?: string;
  }

  /** The icons Fleet maps to its own set. Any other name is invalid. */
  export type IconName =
    | "check" | "x" | "alert" | "info" | "circle" | "dot" | "clock" | "loader" | "play" | "skip"
    | "test" | "bug" | "terminal" | "file" | "folder" | "git-branch" | "search" | "sparkles" | "zap" | "gauge"
    | "arrow-right" | "chevron-right" | "external-link" | "copy" | "eye" | "eye-off";

  export interface ButtonProps {
    /** Unique among the tree's controls. Letters, digits, `_`, `-`, `.`, up to 64. */
    key: string;
    label: string;
    onPress: () => void | Promise<void>;
    icon?: IconName;
    /** As Fleet's notice actions: `primary` filled accent, `danger` red wash, `quiet` text only (the default). */
    tone?: "primary" | "danger" | "quiet";
    disabled?: boolean;
  }

  export interface InputProps {
    key: string;
    label?: string;
    placeholder?: string;
    /** The text the field holds when drawn; the user's typing replaces it until the next draw. */
    value?: string;
    submitLabel?: string;
    onSubmit?: (value: string) => void | Promise<void>;
    onInput?: (value: string) => void | Promise<void>;
  }

  export interface SelectProps {
    key: string;
    label?: string;
    /** 1 to 200, values unique. */
    options: readonly { value: string; label: string }[];
    value?: string;
    onSelect: (value: string) => void | Promise<void>;
  }

  /** Fleet's Markdown, as in the conversation. Links open in a new tab. */
  export interface MarkdownProps {
    key?: string;
    text: string;
    dimColor?: boolean;
  }

  export interface CodeProps {
    source: string;
    language?: string;
    path?: string;
    startLine?: number;
    format?: "source" | "diff";
    wrap?: "wrap" | "truncate";
  }

  /**
   * A page the mod ships, drawn sandboxed the way conversation pages are (opaque origin, Fleet's theme as `--fleet-*`
   * variables, sized to fit). It can't reach Fleet or call back into the mod in Stage 1.
   */
  export interface PageProps {
    key: string;
    /** An `.html` file inside the mod's folder, relative to `mod.json`. */
    path: string;
    title: string;
    /** Added to the page's address as a query string, for the page to read. 4 KiB at most, URL-encoded. */
    query?: Readonly<Record<string, string>>;
  }

  // ─── Limits ──────────────────────────────────────────────────────────────────────────────────────────────────────

  export interface Limits {
    /** A hook's own time per dispatch. */
    readonly hookMs: 10_000;
    /** A `.catch` handler's own time. */
    readonly catchMs: 1_000;
    /** Fleet's wait for a whole dispatch; past it Fleet restarts the host and strikes the mod that was running. */
    readonly dispatchMs: 15_000;
    /** Failures in a row (unanswered by `.catch`) that turn a mod off. */
    readonly strikes: 3;
    /** Text drawn per tree; the rest is cut. */
    readonly treeTextChars: 100_000;
    readonly treeNodes: 2_000;
    readonly treeDepth: 32;
    /** A tree as JSON. Larger is invalid. */
    readonly treeBytes: 262_144;
    readonly storeBytes: 4_194_304;
    readonly stateBytes: 1_048_576;
    readonly invalidatePerSecond: 10;
    readonly timerMinMs: 100;
    readonly timersPerSession: 20;
    readonly moduleBytes: 524_288;
    readonly toastChars: 500;
  }

  // ─── Frozen ──────────────────────────────────────────────────────────────────────────────────────────────────────

  /** Events arrive deeply frozen: assigning to a field throws. Pass a copy to `next` to change one. */
  export type Frozen<T> = T extends (...args: never[]) => unknown ? T : T extends object ? { readonly [K in keyof T]: Frozen<T[K]> } : T;
}

/**
 * The protocol between Fleet and the mod host: JSON-RPC 2.0 over the host's stdin and stdout, one JSON object per line
 * (UTF-8, 8 MiB at most). Both sides send requests, answer them, and send notifications. The host's stderr is Fleet's
 * log. Mods never see these types; Fleet (ModHostClient) and the host (mods/host) are checked against them.
 */
declare module "fleet-mods/protocol" {
  import type { CheckReportHook, EventName, Json, Surface } from "fleet-mods/protocol-shared";

  // ─── Fleet → host requests ───────────────────────────────────────────────────────────────────────────────────────

  export interface FleetToHost {
    /** First message. The host answers with its versions or refuses a protocol it doesn't speak (error -32000). */
    initialize(params: { protocol: 1; fleetVersion: string }): { protocol: 1; hostVersion: string; bunVersion: string };
    /** Reads a module without running it. */
    check(params: { root: string; manifest: string }): CheckReport;
    /**
     * Checks, then loads (or reloads, for the same `id`) a mod: runs `register` and reports what it registered. A mod
     * that fails the check or throws in `register` doesn't load (error -32001, with the report in `data`).
     */
    load(params: LoadParams): LoadResult;
    unload(params: { id: ModId }): Record<string, never>;
    /**
     * Runs one event through the chain of the mods given, in that order, for one session. Fleet only sends events and
     * sites some listed mod registered for, and only a session's own drafts with that session's events.
     */
    dispatch(params: DispatchParams): DispatchResult;
    /** The session is archived or gone: drop its `$.state`, timers and handles. */
    forget(params: { sessionId: string }): Record<string, never>;
    /** Finish what's running (up to 2 s), then exit 0. */
    shutdown(params: Record<string, never>): Record<string, never>;
  }

  /** A kept mod is `name@v3`; a draft is `name@draft:{sessionId}`. */
  export type ModId = string;

  export interface LoadParams {
    id: ModId;
    name: string;
    /** Fleet's version number, or `draft`. */
    version: number | "draft";
    /** The draft's session; absent for a kept mod. */
    sessionId?: string;
    /** The mod's folder (`v{n}/` or the draft folder), holding `mod.json`. */
    root: string;
  }

  export interface LoadResult {
    check: CheckReport;
    /** What `register` actually registered, in order. Fleet routes events with this. */
    hooks: CheckReportHook[];
  }

  export interface DispatchParams {
    event: EventName;
    sessionId: string;
    /** The event as the mods see it, before any hook. */
    e: Json;
    /** The chain, outermost first: kept mods by name, then the session's drafts by name (a draft replaces its kept mod). */
    mods: ModId[];
    /** Where the user was, for ui.press/input/select. */
    surface?: Surface;
  }

  export interface DispatchResult {
    /** The chain's result. For `ui.render`, a WireElement or null; `{ type: "Fleet" }` when every hook passed. */
    result: Json;
    /** Which mods' trees are in a ui.render result, for Fleet's Draft marking. */
    drawnBy?: ModId[];
    /** Hooks that failed during this dispatch. */
    failures: HookFailureReport[];
  }

  export interface HookFailureReport {
    mod: ModId;
    event: EventName;
    kind: "throw" | "timeout";
    message: string;
    /** Failures in a row for this mod, after this one. At 3 the host unloads it and Fleet turns it off. */
    strikes: number;
  }

  // ─── Host → Fleet requests: `$` calls that need Fleet ────────────────────────────────────────────────────────────

  /**
   * `$.state`, `$.clock`, `$.ui.resolve` and `$.ui.invalidate`/`$.ui.log` stay in the host (the last two as
   * notifications). Everything else is a request named after the call. `sessionId` is the session of the event (or the
   * timer, or the callback) the call was made in.
   */
  export interface HostToFleet {
    "store.get"(params: CallContext & { key: string }): { value?: Json };
    "store.set"(params: CallContext & { key: string; value: Json }): Record<string, never>;
    "store.delete"(params: CallContext & { key: string }): Record<string, never>;
    "store.keys"(params: CallContext): { keys: string[] };
    "ui.open"(params: CallContext & { id: string; title?: string }): Record<string, never>;
    "ui.close"(params: CallContext & { id: string }): Record<string, never>;
    "ui.toast"(params: CallContext & { text: string; timeoutMs?: number; tone?: "accent" | "warn" }): Record<string, never>;
    "session.get"(params: CallContext): { id: string; title: string; harness: string; cwd: string; surfaces: Surface[] };
  }

  export interface CallContext {
    mod: ModId;
    sessionId: string;
  }

  // ─── Host → Fleet notifications ──────────────────────────────────────────────────────────────────────────────────

  export interface HostNotifications {
    /** Redraw this mod's sites: in one session, or in every session when `sessionId` is absent. Already throttled. */
    invalidate(params: { mod: ModId; sessionId?: string }): void;
    log(params: { mod: ModId; sessionId?: string; level: "info" | "warn" | "error"; text: string }): void;
    /** A failure outside a dispatch (a timer or a callback threw). Same counting as HookFailureReport. */
    failed(params: HookFailureReport & { sessionId?: string }): void;
  }

  // ─── The static check ────────────────────────────────────────────────────────────────────────────────────────────

  export interface CheckReport {
    ok: boolean;
    name: string;
    /** The manifest's own `version` string. */
    version: string;
    description: string;
    lines: number;
    /** Of the module file, hex. */
    sha256: string;
    /** `hooks:`: each `on(…)` call, with its matcher. */
    hooks: CheckReportHook[];
    /** `calls:`: every `$` call, as `ns.method`, sorted, without repeats (`store.set`, `ui.invalidate`). */
    calls: string[];
    /** Every `$.state` key the module reads or writes. */
    state: string[];
    /** Every `Page` path, each checked to exist inside the mod's folder. */
    pages: string[];
    errors: CheckProblem[];
    warnings: CheckProblem[];
  }

  export interface CheckProblem {
    line?: number;
    column?: number;
    /** Stable, for tests: `import`, `global`, `dollar-escape`, `dynamic-event`, `dynamic-matcher`, … */
    code: string;
    message: string;
  }

  // ─── Trees on the wire ───────────────────────────────────────────────────────────────────────────────────────────

  /**
   * An Element as it leaves the host: children flattened (false/null/undefined dropped, numbers as strings) and every
   * callback replaced by a handle the host keeps until the site is drawn again or the session is forgotten.
   */
  export type WireElement =
    | { type: "Box" | "Text"; props: Record<string, Json>; children: (WireElement | string)[] }
    | { type: "Pill" | "Icon" | "Markdown" | "Code"; props: Record<string, Json> }
    /** `mod`: the id of the mod whose folder `path` is in, from the host's record of who made the element. */
    | { type: "Page"; props: Record<string, Json>; mod: ModId }
    | { type: "Button" | "Input" | "Select"; props: Record<string, Json>; handles: Partial<Record<"onPress" | "onSubmit" | "onInput" | "onSelect", string>> }
    | { type: "Fleet" };
}

/** Shared by the two modules above; not for mods. */
declare module "fleet-mods/protocol-shared" {
  import type { EventName, Json, Surface } from "fleet-mods";
  export type { EventName, Json, Surface };

  export interface CheckReportHook {
    event: EventName;
    /** The matcher as JSON, a RegExp as `{ "$regex": source, "flags": flags }`. */
    matcher?: Json;
  }
}
