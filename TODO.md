# TODO

Tracked future work, sized roughly. Order is rough priority, not commitment.

## Tree-structured sessions (resume + branching)

**Brings.** Resume an existing session and keep going (currently impossible — `_history` dies with the process), and branch off any prior point in a session to try an alternate prompt without losing the original thread. Both branches live in the same file.

**Approach.** Promote the already-reserved `parent_id` field on `SessionEvent` to the spine of a conversation tree. Two-tier overlay: `user_input` / `llm_response` / `tool_result` are tree events the leaf walks; `llm_request` / `tool_call` / `error` are satellites hanging off them. No new file format, no sidecars, no new top-level types — just `parent_id` set everywhere and `schema_version: 2` + `system_prompt` on `session_meta`.

**Effort.** Medium. Five stages, sheddable: (1) schema + writer changes, (2) reader v1→v2 in-memory migration, (3) `/resume` + history rebuild, (4) `/branch` + `/branches`, (5) inspector branches pane. Stages 1–3 are the minimum viable shipment; 4–5 add the branching capability. Total ballpark: a few focused sessions.

**Design doc.** [`docs/sessions-tree.md`](docs/sessions-tree.md) — entry shapes, leaf semantics, migration story, open questions.

**Deferred from v1.** LLM-generated branch summaries (pi-mono's `branch_summary`), compaction, labels, named sessions, full graph visualization in the inspector.
