/**
 * Counts, token counts, sizes and money as Fleet writes them, in the user's locale: "12,500", "17.1k", "3.4 MB",
 * "$1.85". Money is only ever an estimate at list prices, shown where Fleet already shows it (Analytics and a session's
 * cost); never add it somewhere new.
 *
 * Pure functions with no Vue: the phone app can import this file too.
 */

export interface NumberOptions {
  /** For tests and callers that must pin it. Leave out to use the user's locale. */
  locale?: string;
}

export interface CountOptions extends NumberOptions {
  /** Up to this many decimals (none by default): 2 for "1.25 credits". */
  fractionDigits?: number;
}

const formatters = new Map<string, Intl.NumberFormat>();

function numberFormat(options: Intl.NumberFormatOptions, locale: string | undefined): Intl.NumberFormat {
  const key = JSON.stringify([locale ?? null, options]);
  let format = formatters.get(key);
  if (!format) {
    format = new Intl.NumberFormat(locale, options);
    formatters.set(key, format);
  }
  return format;
}

/** A plain count with the locale's grouping, "12,500"; rounded to a whole number unless asked. NaN and infinity are "". */
export function formatCount(n: number | null | undefined, opts: CountOptions = {}): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return "";
  return numberFormat({ maximumFractionDigits: opts.fractionDigits ?? 0 }, opts.locale).format(n);
}

const UNITS = [
  { size: 1e3, suffix: "k" },
  { size: 1e6, suffix: "M" },
  { size: 1e9, suffix: "B" },
] as const;

/**
 * A token count, short: "950", "17.1k", "200k", "1.2M", "3.4B". One decimal under 100 of a unit, none from 100;
 * a count that would round up to the next unit moves to it ("1M", never "1,000k"). NaN and infinity are "".
 */
export function formatTokens(n: number | null | undefined, opts: NumberOptions = {}): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return "";
  const sign = n < 0 ? "-" : "";
  const abs = Math.abs(n);
  if (Math.round(abs) < 1000) return sign + formatCount(abs, opts);
  for (const unit of UNITS) {
    const value = abs / unit.size;
    const digits = value < 99.95 ? 1 : 0;
    const rounded = Number(value.toFixed(digits));
    if (rounded < 1000 || unit.suffix === "B") {
      return sign + numberFormat({ maximumFractionDigits: digits }, opts.locale).format(rounded) + unit.suffix;
    }
  }
  return "";
}

export interface MoneyOptions extends NumberOptions {
  /** ISO 4217, from the data. Fleet's prices are in US dollars. */
  currency?: string;
}

/**
 * An amount of money with two decimals in its currency, "$1.85", "$1,234.50"; "<$0.01" for a positive amount that
 * rounds to nothing, so a cheap call doesn't read as free. The symbol is the narrow one ("$", not "US$") in any locale.
 */
export function formatMoney(amount: number | null | undefined, opts: MoneyOptions = {}): string {
  if (amount === null || amount === undefined || !Number.isFinite(amount)) return "";
  const format = numberFormat(
    { style: "currency", currency: opts.currency ?? "USD", currencyDisplay: "narrowSymbol", minimumFractionDigits: 2, maximumFractionDigits: 2 },
    opts.locale,
  );
  if (amount > 0 && amount < 0.005) return `<${format.format(0.01)}`;
  return format.format(amount);
}

/** A size in bytes: "812 B", "34 KB", "3.4 MB", "340 MB", "2.1 GB". One decimal under 10 from a megabyte up. */
export function formatBytes(bytes: number | null | undefined, opts: NumberOptions = {}): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return "";
  const abs = Math.max(0, bytes);
  if (abs < 1024) return `${formatCount(abs, opts)} B`;
  if (abs < 1024 ** 2) return `${formatCount(abs / 1024, opts)} KB`;
  const [value, unit] = abs < 1024 ** 3 ? [abs / 1024 ** 2, "MB"] : [abs / 1024 ** 3, "GB"];
  const digits = value < 10 ? 1 : 0;
  return `${numberFormat({ minimumFractionDigits: digits, maximumFractionDigits: digits }, opts.locale).format(value)} ${unit}`;
}
