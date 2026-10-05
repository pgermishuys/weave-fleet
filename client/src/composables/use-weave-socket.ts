// Re-export everything from the SignalR implementation
export type {
  Unsubscribe,
  SnapshotCallback,
  DomainEventCallback,
  HistoryCallback,
  WeaveSocketAPI,
} from "./use-signalr-socket"

export {
  useWeaveSocket,
  isWeaveSocketConnected,
  onReconnect,
  onDisconnect,
  onConnectionLost,
  setSessionFocus,
  setPresence,
  loadSessionHistory,
  _resetForTesting,
  _getSubscriberCount,
  _isConnected,
} from "./use-signalr-socket"
