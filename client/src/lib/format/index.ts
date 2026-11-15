/**
 * Formatting for everything Fleet shows the user that isn't words: times, durations, counts and money. Use these
 * instead of Intl or toLocale* in a component, so the same thing reads the same way on every screen (the design lint's
 * AD_HOC_FORMAT rule counts the rest).
 */
export * from "./time";
export * from "./duration";
export * from "./numbers";
