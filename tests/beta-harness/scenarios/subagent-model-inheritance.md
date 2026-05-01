---
id: subagent-model-inheritance
focus: sub-agents, model-selection, persistence
estimated_minutes: 8
---

# subagent-model-inheritance

User-reported bug: sub-agents do not persist their model selection. There is currently no UI
to edit a sub-agent profile.

## Preconditions

- Fleet running with `--harness=test`.
- The model-persistence-refresh playbook has already been validated (so we know the parent
  flow works end-to-end before we add sub-agent fan-out).

## Steps

1. Open the dashboard, create a new session pinned to scenario id
   `subagent-model-inheritance` with provider `acme` and model `acme/turbo-1`.
2. Send a prompt that triggers a sub-agent (e.g. `Delegate to a planner`). The harness
   scenario emits a tool call that the orchestrator interprets as a sub-agent spawn.
3. Wait for the parent session to enter "delegating" state (status indicator) and a
   sub-agent breadcrumb / link to appear in the UI.
4. Click into the sub-agent session.
5. Inspect the sub-agent session detail page:
   - Does the model selector default to the parent's `acme/turbo-1`, or to a generic
     default?
   - Does the assistant echo include `acme/turbo-1`?
6. Reload the sub-agent page.
7. Send another prompt to the sub-agent.
   - Does the assistant echo still include `acme/turbo-1`?
8. Record a finding.

## What to watch

- Network: `POST /api/sessions` for the SUB-agent — does the request include the parent's
  model id?
- Server-side: the harness only emits a `delegation.created` event. Whether the orchestrator
  copies the parent's model into the child's `HarnessSpawnOptions` is a server concern.
- UI: a "model" badge or selector on the sub-agent header.

## Known suspects

- The parent's model is not propagated to `EnsureDelegatedChildSessionAsync`.
- The sub-agent UI has no model selector at all and silently uses the harness default.
- The sub-agent's first prompt request does not include `model` in its payload.

## Scenario JSON

This is a stub — sub-agent fan-out is not yet wired through the file-driven harness. For
now the playbook treats the parent like the model-persistence scenario; the sub-agent
fan-out hook is captured as a TODO so a follow-up commit can extend `LiveScenarioHarness`
with a `subAgents` field.

```json
{
  "promptResponses": [
    {
      "events": [
        {
          "type": "session.status",
          "delayMs": 0,
          "payload": {
            "sessionId": "_placeholder_",
            "status": { "type": "busy" }
          }
        },
        {
          "type": "message.updated",
          "delayMs": 0,
          "payload": {
            "info": {
              "id": "msg-parent-user-1",
              "sessionID": "_placeholder_",
              "role": "user"
            }
          }
        },
        {
          "type": "message.part.updated",
          "delayMs": 0,
          "payload": {
            "sessionID": "_placeholder_",
            "part": {
              "id": "msg-parent-user-1-text",
              "sessionID": "_placeholder_",
              "messageID": "msg-parent-user-1",
              "type": "text",
              "text": "_user_prompt_"
            }
          }
        },
        {
          "type": "message.updated",
          "delayMs": 100,
          "payload": {
            "info": {
              "id": "msg-parent-assistant-1",
              "sessionID": "_placeholder_",
              "role": "assistant"
            }
          }
        },
        {
          "type": "message.part.updated",
          "delayMs": 100,
          "payload": {
            "sessionID": "_placeholder_",
            "part": {
              "id": "msg-parent-assistant-1-text",
              "sessionID": "_placeholder_",
              "messageID": "msg-parent-assistant-1",
              "type": "text",
              "text": "[beta-harness] sub-agent fan-out is not yet driven by the file harness. Once LiveScenarioHarness gains a subAgents field, this response should emit a delegation.created event and a child session id."
            }
          }
        },
        {
          "type": "session.idle",
          "delayMs": 100,
          "payload": {
            "sessionId": "_placeholder_",
            "status": { "type": "idle" }
          }
        }
      ]
    }
  ]
}
```
