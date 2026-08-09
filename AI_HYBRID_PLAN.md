# Ask AI Hybrid Architecture

This document describes the implemented architecture. Ask AI is not a keyword-only router and does not entrust calculations, mutation authority, or delivery state to model prose.

## Request pipeline

1. `AiIntentResolver` performs cheap deterministic extraction for exact ledger shorthand, explicit dates, filters, and structured contextual presets.
2. `AiCapabilities` is the single registry for every supported intent, topic, and sensitive-data requirement. Schemas, sanitization, and policy derive from it.
3. Ambiguous, multi-domain, short semantic, and collision-prone requests use the classifier. A valid classifier result is authoritative; deterministic keyword confidence is not treated as calibrated probability.
4. Dedicated loaders fetch bounded, server-owned evidence. Structured invocation references select the surface, cycle, investment range, or savings goal without relying on those words being present in the prompt.
5. The server computes aggregates and completeness metadata. “No matching rows” remains distinct from “the cycle has no rows,” and exact counts are never paired with partial totals.
6. The answer model receives only the bounded evidence required by the capability. Action output is validated independently from reply text.

Keywords remain useful as a fast extraction signal, but they are neither the sole routing mechanism nor an authorization boundary.

## Conversation continuity

Conversation history and structured follow-up state are server-owned until the user successfully starts a new chat. State has explicit topics for ledger, recurring payments, wishlist, Rewards and savings goals, investments, and reports. Every field is sanitized and round-tripped; switching domains does not inherit stale ledger facets.

Each client turn is reserved as `Pending` before any provider request, then becomes `Completed` or is removed on failure. This prevents concurrent requests with the same `clientTurnId` from making duplicate provider calls. Prompt history uses completed turns only.

Hydration is intentionally bounded. Long-term continuity should be extended through structured state or summaries, not by sending an ever-growing raw transcript.

## Actions and truthful replies

Model output proposes typed actions; it never directly mutates financial records. The server validates command polarity, required fields, known record identifiers, sensitive mode, and action limits. Direct recurring changes still open the normal application confirmation UI.

Draft claims are derived from accepted action counts. If no draft action survives validation, the user receives an honest deterministic reply rather than a model claim that something was prepared.

V2 clients receive stable action IDs in a durable action batch. An unresolved batch is included in conversation hydration and remains available until the client reports that it was accepted or dismissed. This covers cancellation, refresh, network ambiguity, and frontend dispatch failure. The client must treat action delivery as idempotent.

## Privacy

Sensitive mode is monotonic for an in-flight request: the server-persisted setting or the client's stricter `forceSensitiveMode` flag can hide data, but a stale client cannot reveal it. Amount-revealing capabilities are rejected before the answer-model call. The setting is checked again after a provider call, raw hidden-mode user text is not persisted, and the frontend clears and rehydrates visible history when hiding is enabled.

## Cost controls

Exact commands and deterministic guards require no classifier. Semantic classification is reserved for requests where lexical routing is unsafe. Ledger draft categories emitted by the constrained answer schema are validated and used directly; Ask AI does not make one extra category-model call per draft.

## Required regression coverage

- every intent enum appears exactly once in the capability registry and structured-output schema;
- every conversation-state field survives sanitize, resolve, persistence, and hydration;
- domain switching and short follow-ups do not inherit unrelated filters;
- collision prompts such as “goals,” “bills,” “afford,” and “room” use semantic planning;
- negated, hypothetical, malformed, or unknown mutation actions are rejected;
- reply draft counts equal accepted action counts;
- a pending duplicate turn does not call the provider twice;
- unresolved action batches survive hydration and disappear only after resolution;
- sensitive-mode races cannot reveal or persist raw sensitive text;
- non-empty cycles with zero search matches are not reported as empty;
- provider call-count tests protect the deterministic fast paths.
