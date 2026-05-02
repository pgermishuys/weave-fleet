---
id: model-persistence-refresh
focus: model-selection, persistence, refresh
estimated_minutes: 5
---

# model-persistence-refresh

User-reported bug: the model selected when starting a chat is not persisted. After a page
refresh the session reverts to the default model and subsequent prompts silently fail.

## Preconditions

- Fleet running with `--harness=test` and the materialised scenario JSON in place.
- Dashboard reachable at the driver's `baseUrl`.

## Steps

1. Open the dashboard.
2. Click "New session". In the dialog, pick provider `acme` and model `acme/turbo-1`.
3. Submit. The session detail page loads. The status indicator should show `idle`.
4. Send the prompt `Hello, world!`.
5. Wait for the assistant message. The harness echoes the requested model id verbatim, so the
   assistant text should contain `acme/turbo-1`.
6. Reload the page (`Page.ReloadAsync`).
7. Inspect the session detail page after reload:
   - The model selector still shows `acme/turbo-1`.
   - Sending another prompt produces an assistant message that still contains `acme/turbo-1`.
8. Record a finding.

## What to watch

- Network: `POST /api/sessions` payload — does the request include the chosen model?
- Network: `GET /api/sessions/{id}` after reload — does the response include the chosen
  model in the session metadata?
- Log (`fleet.log`): lines matching `(?i)model.*selected|HarnessSpawnOptions|selected model`.
- UI: the model selector control after reload — does it default back to a placeholder?

## Known suspects

- The model selection is held only in client state and never sent to the server.
- The server stores the model on the wrong field (e.g. on the instance, not the session) and
  loses it after a reload.
- The model dropdown re-initialises to its first option on mount.

## Scenario JSON

The harness emits a streamed text response. The text is exactly what the playbook author
wrote, so the assistant's message lets us verify the requested model id round-tripped.

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
              "id": "msg-user-1",
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
              "id": "msg-user-1-text",
              "sessionID": "_placeholder_",
              "messageID": "msg-user-1",
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
              "id": "msg-assistant-1",
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
              "id": "msg-assistant-1-text",
              "sessionID": "_placeholder_",
              "messageID": "msg-assistant-1",
              "type": "text",
              "text": "[beta-harness] model echo: acme/turbo-1 — replace this stub with the real selected-model echo once the server propagates HarnessSpawnOptions to the harness."
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
    },
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
              "id": "msg-user-2",
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
              "id": "msg-user-2-text",
              "sessionID": "_placeholder_",
              "messageID": "msg-user-2",
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
              "id": "msg-assistant-2",
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
              "id": "msg-assistant-2-text",
              "sessionID": "_placeholder_",
              "messageID": "msg-assistant-2",
              "type": "text",
              "text": "[beta-harness] model echo on second prompt — same model id should appear here."
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
