-- Persist the model selection per session so a SPA refresh (which loses local state)
-- doesn't silently fall back to the harness default on the next prompt.
-- Populated on every successful prompt that carries a model; consulted by
-- ResolveSessionModelAsync as a fallback when the prompt request omits one.
ALTER TABLE sessions ADD COLUMN selected_provider_id TEXT;
ALTER TABLE sessions ADD COLUMN selected_model_id TEXT;
