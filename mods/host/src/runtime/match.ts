const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

function valueMatches(want: unknown, have: unknown): boolean {
  if (Array.isArray(want)) return want.some((w) => valueMatches(w, have));
  if (want instanceof RegExp) {
    if (typeof have !== "string") return false;
    want.lastIndex = 0;
    return want.test(have);
  }
  if (isObject(want)) return isObject(have) && matches(want, have);
  return want === have;
}

/** Does `e` satisfy `matcher`? Every matcher field must match the same field of `e`; no matcher matches everything. */
export function matches(matcher: Record<string, unknown> | undefined, e: unknown): boolean {
  if (matcher === undefined) return true;
  if (!isObject(e)) return false;
  for (const key of Object.keys(matcher)) {
    if (!(key in e)) return false;
    if (!valueMatches(matcher[key], e[key])) return false;
  }
  return true;
}

/** A matcher as JSON: a RegExp becomes `{ "$regex", "flags" }`. */
export function matcherToJson(value: unknown): unknown {
  if (value instanceof RegExp) return { $regex: value.source, flags: value.flags };
  if (Array.isArray(value)) return value.map(matcherToJson);
  if (isObject(value)) return Object.fromEntries(Object.keys(value).map((k) => [k, matcherToJson(value[k])]));
  return value;
}
