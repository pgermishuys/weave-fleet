/**
 * Shares a request between callers asking for the same key (`keyOf` their arguments) while it is still in flight. Opening a session, the
 * composer, header, conversation and prompt helpers each ask for its models and agents at once; one request answers
 * them all. Nothing is kept once it settles, so the next ask goes to the server.
 */
export function shareInFlight<A extends unknown[], T>(
  load: (...args: A) => Promise<T>,
  keyOf: (...args: A) => string,
): (...args: A) => Promise<T> {
  const inFlight = new Map<string, Promise<T>>();

  return (...args) => {
    const key = keyOf(...args);
    const pending = inFlight.get(key);
    if (pending) {
      return pending;
    }

    const request = load(...args).finally(() => {
      inFlight.delete(key);
    });
    inFlight.set(key, request);
    return request;
  };
}
