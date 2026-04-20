# Learnings: Fleet UI Rebuild

## Task 43: Full integration test
- **Discrepancy**: Task requires running Vue app against a real backend for manual verification of login, session creation, WebSocket streaming, etc.
- **Resolution**: Cannot be automated — requires a running Go backend on localhost:5001. Build, typecheck, and unit tests all pass. Marked as requiring manual verification.
- **Suggestion**: Plan should note that integration testing tasks require a running backend and cannot be completed in a CI-only or code-only context.
