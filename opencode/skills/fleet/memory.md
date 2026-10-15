# Memory

Offer this when the user says "remember…", "from now on…" or states a preference that will matter again. Do this
before you save it anywhere else, including the harness's own memory: Fleet's memory is shared by every harness and
the user sees and edits it in Settings.

**Check first**: `curl -s "$FLEET_URL/api/memory"` and read `enabled`.

- **Off**: say memory is off, so you can't promise to remember it, and that it's switched on in Settings → Memory.
  Don't switch it on, and don't write the note anywhere else as if it were Fleet's memory. Offer to put it in the
  repository's `AGENTS.md` or `CLAUDE.md` if they want it kept now, and ask first.
- **On**: you have a `fleet_memory_save` tool, and your instructions say what to save. Use the tool, not the API.
  `GET /api/memory/notes?repository=<folder>` lists the notes a session there reads. The user can see, edit and
  forget every note in Settings → Memory.

Notes are per repository or per machine. Learned notes expire after days of use unless the user keeps them. Never
save secrets.
