-- Migration 071: Every address a machine answers on.
-- A JSON array of {url, kind, learned, lastResult, lastTriedAt} in the order Fleet tries them; base_url stays the one
-- in use. Null for a machine saved before this, which keeps its one address until it next reports its own.

ALTER TABLE machines ADD COLUMN addresses TEXT;
