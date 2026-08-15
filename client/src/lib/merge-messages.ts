/**
 * Merge delivered and optimistic messages.
 *
 * Server order is authoritative. Delivered messages come first (in server order),
 * followed by optimistic messages (in client order).
 */

export function mergeMessagesByTimestamp<T extends { createdAt?: number }>(
  delivered: readonly T[],
  optimistic: readonly T[],
): T[] {
  return [...delivered, ...optimistic];
}
