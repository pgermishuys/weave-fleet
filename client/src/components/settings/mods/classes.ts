const BASE =
  "inline-flex items-center justify-center rounded-md px-3 py-1.5 text-sm font-medium transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60";

/** The filled button of a Mods panel. */
export const PRIMARY_BUTTON = `${BASE} bg-accent text-white hover:opacity-90`;
/** Its plain companions: Cancel, Back, Copy. */
export const SECONDARY_BUTTON = `${BASE} border border-border bg-card-bg text-text hover:border-accent/50`;
/** Inline code in a panel's text. */
export const CODE = "font-mono text-[0.8rem] text-text";
