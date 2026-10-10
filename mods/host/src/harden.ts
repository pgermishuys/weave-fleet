/**
 * Locks the host's JavaScript realm down before any mod is imported (docs/mods/api.md, "The static check"). The check
 * reads what a mod names; this closes what it could build at run time: SES's lockdown freezes every intrinsic (so no
 * mod can change `Object.prototype` or `Promise` under the host or another mod) and makes the constructors of every
 * function kind inert (so no string becomes code through `fn.constructor`). Then the start realm's own `Function` and
 * `eval`, which lockdown leaves working, are taken away too.
 */
import "ses";

let hardened = false;

export function hardenRealm(): void {
  if (hardened) return;
  hardened = true;
  lockdown({
    // The host reports errors and rejections itself; SES's traps would exit the process.
    errorTrapping: "none",
    unhandledRejectionTrapping: "none",
    reporting: "none",
    // Keep `error.stack` and the console as they are: the host routes both.
    errorTaming: "unsafe",
    consoleTaming: "unsafe",
    // Mods may format numbers and dates for the user's locale.
    localeTaming: "unsafe",
    overrideTaming: "severe",
  });
  const inert = Function.prototype.constructor;
  Object.defineProperty(globalThis, "Function", { value: inert, writable: false, enumerable: false, configurable: false });
  Object.defineProperty(globalThis, "eval", { value: undefined, writable: false, enumerable: false, configurable: false });
}
