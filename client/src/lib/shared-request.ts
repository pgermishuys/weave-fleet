/**
 * Shares a request between callers asking for the same key while it is still in flight. Opening a session, the
 * composer, header, conversation and prompt helpers each ask for its models and agents at once; one request answers
 * them all. Nothing is kept once it settles, so the next ask goes to the server.
 */
export function shareInFlight<T>(load: (key: string) => Promise<T>): (key: string) => Promise<T> {
  const inFlight = new Map<string, Promise<T>>();

  return (key) => {
    const pending = inFlight.get(key);
    if (pending) {
      return pending;
    }

    const request = load(key).finally(() => {
      inFlight.delete(key);
    });
    inFlight.set(key, request);
    return request;
  };
}
