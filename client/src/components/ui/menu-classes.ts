/*
 * One look for every menu built on reka's menus: right-click menus and dropdowns. It matches Fleet's chip menus
 * (.ns-pop and .ns-option in new-session.css): the raised surface, a soft text tint on the highlighted row, and colour
 * only where it means something (red for a destructive row).
 */

const panel = "z-50 min-w-[8rem] overflow-x-hidden overflow-y-auto rounded-card border border-border bg-card-bg p-[5px] text-[13px] text-text shadow-(--menu-shadow) outline-hidden";

const motion = "duration-[120ms] data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=open]:fade-in-0 data-[state=closed]:fade-out-0 data-[state=open]:zoom-in-[0.97] data-[state=closed]:zoom-out-[0.97]";

/** The menu panel. Pass the reka transform-origin variable so it grows from where it opened. */
export const menuContentClass = `${panel} ${motion}`;

const row = "relative flex min-h-[30px] cursor-default items-center gap-2 rounded-[calc(var(--radius-btn)-2px)] px-2 py-[5px] text-[13px] outline-hidden select-none transition-colors duration-75 data-[highlighted]:bg-text/7 data-[disabled]:pointer-events-none data-[disabled]:opacity-45 data-[inset]:pl-8 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-3.5 [&_svg:not([class*='text-'])]:text-muted data-[highlighted]:[&_svg:not([class*='text-'])]:text-text";

/** A row. `data-variant="destructive"` turns it red, with a red tint when highlighted. */
export const menuItemClass = `${row} data-[variant=destructive]:text-destructive data-[variant=destructive]:data-[highlighted]:bg-destructive/12 data-[variant=destructive]:[&_svg]:!text-destructive`;

/** A row that opens a submenu; it stays tinted while its submenu is open. */
export const menuSubTriggerClass = `${row} data-[state=open]:bg-text/7`;

/** A row with a check or dot in front of it. */
export const menuIndicatorItemClass = `${row} pl-8`;

export const menuSeparatorClass = "mx-0.5 my-1 h-px bg-border";

export const menuLabelClass = "px-2 pt-[7px] pb-[3px] text-[11px] font-semibold tracking-[0.05em] text-muted uppercase data-[inset]:pl-8";

/** A key chip, as in the status bar. */
export const menuShortcutClass = "ml-auto inline-flex items-center rounded-[calc(var(--radius-btn)-3px)] border border-border bg-text/6 px-[5px] py-[2px] text-[10px] leading-none font-medium text-muted";
