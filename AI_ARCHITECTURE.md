# Ask AI architecture

Ask AI answers questions about the signed-in user's own money and prepares changes the user confirms. The model chooses its own read-only tools; the server owns every figure, every rule about what may change, and the conversation record.

## Turn pipeline

1. **Guards** (`AiAssistantService.AgentTurn.cs`). Empty or overlong messages, invalid invocation context, small talk, an unconfigured provider, and the daily token budget are answered without a model call. In sensitive mode a bare record list ("Coffee 12") is refused up front; every other change is refused by action validation.
2. **Context.** `AiToolContextFactory` fixes the turn's cycle day, currency, today, and sensitive mode. `AiBaselineSnapshotBuilder` sends a compact snapshot on every turn: a 12-cycle calendar (key → date range), accounts, the current cycle's totals and top spending, the next bills, category limits needing attention, and the first saved transaction date. Each part comes from the same tool the model can call; a failing part is left out.
3. **Loop** (`AiAgentEngine`). Up to four rounds against the Responses API with function tools. Reasoning items are echoed between rounds (`store: false`, `reasoning.encrypted_content`). At the limit the model must answer from what it has. Tool calls run sequentially because the scoped `DbContext` is not thread-safe.
4. **Actions.** The model proposes UI actions through `propose_ui_actions`. Each action goes through `ValidateActionAsync` (the shared action rules) against a validation context holding only records a tool surfaced this turn, re-read from the database. The verdict, with a reason for each rejection, goes back to the model so it can correct itself in the same turn.
5. **Post-processing.** Ledger drafts are enriched deterministically (explicit per-line hints, account mentions). Draft claims are rewritten from the accepted action count. A truncated or approximate lookup forces approximate wording. The outgoing state carries the transactions this turn surfaced, invocation references, and any unanswered record request.

## Fast paths

- A message that is only a record list (`CountLedgerDraftListRecords`) forces the first round to `propose_ui_actions` and pins the draft count.
- A screen's "Explain this" preset runs its obvious lookup before the first round (`analyze_transactions` review, `get_investments`, `get_goals_and_rewards`, `get_loans`).

## Tools

| Tool | Purpose |
|---|---|
| `search_transactions` | Text, date, cycle, category, bucket, type, amount, and account search over **all history by default**, with exact SQL count and totals over every match and an exact → spacing → fuzzy match ladder. |
| `get_purchase_pattern` | Purchase cadence, last and next expected date. |
| `get_cycle_summary` / `compare_cycles` | Cycle cash flow, per-bucket net, progress insights; server-computed differences between cycles. |
| `get_spending_breakdown` | Grouping by category, bucket, merchant, account, weekday, or day. |
| `analyze_transactions` | Unusual spending, likely duplicates, and the report review. |
| `get_accounts` | Account and bucket balances from `LedgerAccountService`. |
| `get_recurring` | Recurring payments with occurrence-derived next due dates and normalized cost. |
| `get_category_limits` | Effective-dated limit progress and projection. |
| `get_goals_and_rewards` | Savings-goal pools and pace; wishlist affordability and the Wishlist page's forecast. |
| `get_budget_plan` | Allocation and stability-fund position. |
| `forecast_ledger_balance` | Time for a bucket to reach a target. |
| `get_loans` / `get_investments` | Loan terms and replay; stored portfolio calculations. |

Each tool is one file under `Services/AI/Tools`, wraps existing services or pure calculators, records the ids it returns as evidence, and declares its sensitive-mode behaviour. `AiToolExecutor` turns argument mistakes, budget exhaustion, and server failures into model-readable errors and strips money fields at any depth in sensitive mode.

## Streaming

`POST /api/ai/chat/stream` emits `status`, `delta`, `reset`, then `done` (the authoritative `AiChatResponse`) or `error` (with the status the JSON endpoint would use). A keep-alive comment is sent every 15 seconds. `POST /api/ai/chat` runs the same turn without progress. The client falls back to the JSON endpoint when the stream endpoint is missing and reads the body whole when the runtime cannot stream it.

## Memory, retention, and cost

Conversation memory is server-owned once a client sends a `clientTurnId`. Prior turns are replayed as real dialogue together with a compact note of the lookups each turn made (`ToolTraceJson`, never kept for sensitive turns). Completed turns and daily usage rows are pruned by `LedgerRetentionService` on indexed keys. A turn left Pending by a crash is released after ten minutes. `AiUsageMeter` records tokens per user per UTC day.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `OpenAiModels:Chat` | `OpenAiModel` | Model for Ask AI turns. |
| `Ai:RequestsPerMinute` | 20 | Rate limit for `/api/ai/*`. |
| `Retention:AiTurnRetentionDays` | 90 | Age at which conversation turns are pruned. |
| `Retention:AiUsageRetentionDays` | 60 | Age at which daily usage rows are pruned. |

## Required regression coverage

- tool defaults widen scope (all history), and "no match" stays distinct from "no data";
- every tool leaks no seeded amount in sensitive mode;
- an id the model never looked up, or a change the user never asked for, is rejected;
- the round limit stops a turn;
- the stream's `done` event is authoritative and the fallback path works;
- retention prunes aged and abandoned turns.
