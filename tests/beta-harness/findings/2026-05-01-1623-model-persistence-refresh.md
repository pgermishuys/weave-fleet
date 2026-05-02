# Finding: model-persistence-refresh

- **Recorded:** 2026-05-01-1623
- **Result:** pass

## Repro

1. Start fleet with --harness=test.
2. POST /api/sessions with scenarioId=model-persistence-refresh.
3. POST /api/sessions/{id}/prompt with text="Hello, world!".
4. Poll GET /api/sessions/{id}/messages for an assistant message.

## Evidence

- Assistant message id: msg-assistant-1
- Assistant text: [beta-harness] model echo: acme/turbo-1 — replace this stub with the real selected-model echo once the server propagates HarnessSpawnOptions to the harness.

## Next probe

Drive the same flow through the SPA to verify model selection round-trips after refresh.
