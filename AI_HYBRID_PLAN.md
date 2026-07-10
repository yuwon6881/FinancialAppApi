# AI Assistant Hybrid Architecture — Assessment, Verification & Implementation Plan

Scope: `FinancialAppApi/Services/AiAssistantService.cs` and its tests.
Reality check: the code has already implemented much of the "recommended" design
(explicit intents, confidence scoring, classifier fallback, deterministic aggregates).
This plan targets the **remaining gaps** and how to prove the whole thing is correct.

---

## 1. Assessment of the design

### Verdict
The architecture is sound and it is the right direction. The core idea — *separate
"understand the request" from "decide what data" from "compute the result"* — is exactly
what keeps an LLM feature testable and cheap. Nothing in the doc is wrong. But because
~60% of it already exists in your code, the value is now concentrated in three specific
gaps, not a rewrite.

### Where I agree strongly
- **Deterministic calculation over model arithmetic (Layer 4).** This is the highest-value
  principle and you should finish it. See the concrete gap below.
- **Sufficiency check + second *fetch* (Layer 5).** This is the only part that actually
  fixes the doc's motivating weakness ("cannot recover from under-fetching"). Everything
  else is polish.
- **`TransactionDataLevel` enum.** Replacing the `NeedsTransactionDetail && !aggregateOnly`
  tangle (lines 434–436) with `None | AggregateOnly | MatchingRows | BoundedSample` is a
  genuine clarity win, not just a rename.

### Concrete gaps I found in the current code (grounded, not hypothetical)
1. **The badminton count is still computed by the model, not the server.** The system
   instruction (line 1345) literally tells the model to "count matching transaction
   descriptions … from recentTransactions." The server loads the rows but never produces a
   `matchCount`. This is precisely the Layer-4 gap the doc's lead example is about — it is
   *not* yet fixed in your code.
2. **Wishlist forecast uses mean, not median, and includes the incomplete cycle.**
   `BuildWishlistForecast` (line 783) does `perCycleSavings = savings / cycleCount` over
   whatever cycles were loaded. The doc's recommendation (median positive net savings over
   *complete* cycles, excluding the current partial cycle) is a real, unimplemented
   improvement.
3. **No sufficiency validation.** If `ResolveTargetCycles` returns `[]` and the recent-500
   fallback (line 540) doesn't contain the matching row, the model gets an empty block and
   can only guess/refuse. There is no re-query.

### Where I'd push back / cautions
- **Gate the second *model* call hard; keep the second *fetch* cheap.** Most sufficiency
  failures are fixable by re-querying Postgres, not by re-prompting Gemini. Your
  `TryResolveLedgerEditAsync` already demonstrates "resolve server-side, return with zero
  model calls." Follow that pattern: a re-fetch should almost never trigger a second LLM
  round-trip.
- **Measure the classifier before investing more in it.** Phase 5 of the doc says to log
  classifier usage/confidence. You don't log it today. Add that telemetry first — the
  deterministic router may already handle ~95% of traffic, in which case the classifier is
  a cost you want to keep rare, not expand.
- **Don't let `AiQueryPlan` become a second source of truth.** Today `DetermineContextNeeds`
  and `MergeClassifiedPlan` both mutate one `ContextNeeds`. If you add a plan layer, make
  the plan the *only* thing `BuildContextAsync` reads. Two overlapping selectors is the exact
  "one expression suppresses another" fragility the doc warns about.
- **Structured conversation state has a privacy dimension.** The endpoint is currently
  stateless; history is client-supplied and sanitized (line 299). Server-side state means
  storage + trust decisions. Follow the doc's own rule: store *references and query params*
  (cycle, searchText, matched IDs), never amounts or records; reload fresh from the DB. Do
  this last — it's the biggest new surface for the smallest correctness gain.
- **Your best existing asset is the completeness flag, not the classifier.**
  `DataScope.aggregatesCoverAllTransactionsInRequestedCycles` and
  `requestedCycles[].hasTransactions` are what stop hallucination. Lean into them.

### Gaps ranked by value
1. **Finish Layer 4** — server-computed `activityCount` (badminton) + median wishlist
   forecast. Cheap, directly fixes correctness, easy to unit-test.
2. **Layer 5 sufficiency + re-fetch** — the real differentiator.
3. **Phase 2 `AiQueryPlan` + `TransactionDataLevel`** — clarity + testability.
4. **Phase 3 completion** — extract calcs into a DB-free `AiMetricsCalculator` for exhaustive
   unit testing.
5. **Layer 7 structured state** — do last, guard privacy.

---

## 2. How to verify the code once it's done

### The testing seam you already have (and should build on)
`CapturingHandler` in `AiAssistantHistoricalContextTests.cs` intercepts the HTTP call and
exposes `UserContent` — the **exact prompt sent to the model**. That lets you assert on
*what the server decided to send* without a live model. This cleanly separates:

- **"Did the pipeline build the right context?"** — deterministic, fully unit-testable.
- **"Did the model phrase the answer well?"** — not unit-testable; verify manually/e2e.

Keep that split. Almost all value is in the first bucket.

### Five verification layers
1. **Unit (no DB, no HTTP):** pure domain functions — calculations, cycle resolution, intent
   routing, the sufficiency validator. Fast and exhaustive.
2. **Service integration (EF InMemory + scripted handler):** `ChatAsync` end to end, asserting
   on the captured prompt and returned `Actions`. This is where `AiAssistantHistoricalContextTests`
   already lives — extend it heavily.
3. **Multi-call flows:** the re-fetch loop and classifier fallback need a handler that returns
   *different* responses per call and records call count + per-call content. The current
   `CapturingHandler` only keeps the last call and returns one canned reply — **you must
   upgrade it** (see §3).
4. **Golden regression corpus:** a `[Theory]` table of `(message, history) → expected
   {intents, targetCycles, dataLevel, actions}`. Every new regex must keep the whole corpus
   green — this is your guard against the "each pattern breaks another" failure mode.
5. **Manual / e2e:** use the `/run` and `/verify` skills to drive the real `/api/ai/chat`
   endpoint against a seeded DB for a handful of golden cases, confirming model-facing
   behavior for real.

### Definition of done (per gap)
- **Layer 4:** `AiMetricsCalculator.ActivityCount(rows, "badminton", range)` returns 4 with a
  unit test; the prompt contains a server-computed `matchCount`, and the system instruction no
  longer asks the model to count.
- **Layer 5:** a test proves that a question whose target rows are missing triggers a **second
  DB fetch and zero extra model calls**, and that the final prompt contains the rows.
- **Phase 2:** `BuildContextAsync` reads only from `AiQueryPlan`; the 7 booleans are gone or
  private to the planner; corpus stays green.
- **Layer 7:** "the cycle before that" resolves to the correct month using stored state, proven
  by a two-turn test; state contains no amounts.

### Gotcha
The classifier and the answer model share one `AiClient`/handler. Existing tests never hit the
classifier because confidence stays ≥ 0.72. To test the classifier path you must either craft a
genuinely ambiguous message (to force low confidence) **or** queue a classifier-shaped JSON as
the first scripted reply and a chat-shaped JSON as the second.

---

## 3. Test infrastructure to add

Replace the per-test `CapturingHandler` with one shared scripted handler that supports
multi-call flows. Put it in `TestHelpers` (or a new `AiTestHarness.cs`).

```csharp
// Records every request; returns queued model replies in order (repeats the last one
// once the queue is down to one entry, so single-call tests need only one reply).
internal sealed class ScriptedAiHandler : HttpMessageHandler
{
    private readonly Queue<string> _replies;
    public List<string> RequestBodies { get; } = new();
    public List<string> UserContents { get; } = new();
    public int CallCount => RequestBodies.Count;
    public string LastUserContent => UserContents[^1];

    public ScriptedAiHandler(params string[] replies)
        => _replies = new Queue<string>(replies.Length == 0
            ? new[] { Chat("ok") }
            : replies);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        RequestBodies.Add(body);
        using var doc = JsonDocument.Parse(body);
        UserContents.Add(doc.RootElement
            .GetProperty("contents")[0].GetProperty("parts")[0]
            .GetProperty("text").GetString() ?? "");

        var reply = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
        var providerBody = JsonSerializer.Serialize(new
        {
            candidates = new[] { new {
                content = new { parts = new[] { new { text = reply } } },
                finishReason = "STOP" } }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(providerBody, Encoding.UTF8, "application/json")
        };
    }

    // Helpers to build well-formed provider payloads for each call type.
    public static string Chat(string reply, string actionsJson = "[]", bool close = false)
        => $"{{\"reply\":{JsonSerializer.Serialize(reply)},\"closeChat\":{close.ToString().ToLower()},\"actions\":{actionsJson}}}";

    public static string Classifier(string[] intents, double confidence,
        string? searchText = null, string? cycleHint = null)
        => JsonSerializer.Serialize(new { intents, confidence, searchText, cycleHint });

    // True for the classifier round-trip (its prompt starts with "Classify the user's").
    public bool WasClassifierCall(int index) =>
        UserContents[index].Contains("Classify the user's");
}
```

Add a factory to keep tests short:

```csharp
public static AiAssistantService NewAiService(
    AppDbContext context, ScriptedAiHandler handler)
{
    var cache = new MemoryCache(new MemoryCacheOptions());
    var client = new AiClient(new HttpClient(handler),
        NewConfiguration(("AiApiKey", "key"), ("AiModel", "test-model")),
        NullLogger<AiClient>.Instance);
    return new AiAssistantService(client, context, new TransactionCategoryService(context, cache));
}
```

---

## 4. Comprehensive test suite (matrix)

Grouped by layer. `⊕` = extends an existing test; `＋` = new. Each row is one `[Fact]`
unless marked `[Theory]`.

### A. Guardrails & small talk (regression — already partly covered)
| # | Test | Asserts |
|---|---|---|
| A1 ⊕ | Empty/whitespace message | canned prompt, `CallCount == 0` |
| A2 ＋ | Message > `MaxMessageLength` | "too long", 0 calls |
| A3 ＋ `[Theory]` | delete/remove/erase/cancel/discard commands | refusal, 0 calls |
| A4 ＋ `[Theory]` | "why is X redundant", "should I delete" (negation cases) | NOT refused — reaches model |
| A5 ＋ `[Theory]` | greeting / farewell / ack exact-match list | canned reply, `CloseChat` only on farewell, 0 calls |
| A6 ＋ | "thanks for finding my food spend" (ack word mid-sentence) | reaches model, not treated as small talk |
| A7 ＋ | `!_aiClient.IsConfigured` | "not configured", 0 calls |

### B. Layer 1 — deterministic parsing / cycle resolution
| # | Test | Asserts |
|---|---|---|
| B1 ＋ `[Theory]` | ISO `2026-03`, "March 2023", "in 2024", "last year", "this year", "3 cycles ago", "cycle before last", "past 6 months", "this/previous cycle", named month | `ResolveTargetCycles` returns exact `CycleKey` set + `ExplicitlyRequested` flag |
| B2 ⊕ | explicit range "March 2023 to May 2023" | intermediate April included (exists) |
| B3 ＋ | range cap: "Jan 2000 to Jan 2010" | clamped to ≤ 24 cycles |
| B4 ＋ `[Theory]` | date extraction: `2023-04-05`, "April 5, 2023", "5th April", no-year → `defaultYear` | correct `DateOnly`, invalid (Feb 30) rejected |
| B5 ＋ | no cycle clue + detail request ("find Grab") | falls back to recent-500 path, `ExplicitlyRequested == false` |

### C. Layer 2 — intent routing & confidence
| # | Test | Asserts |
|---|---|---|
| C1 ＋ `[Theory]` | one-liner per intent (activity_count, spending_total, comparison, transaction_list, edit, wishlist.list/forecast, recurring.list/upcoming, allocation.balance/performance, navigation) | expected intent present, confidence ≥ 0.72, `CallCount == 1` (no classifier) |
| C2 ＋ | ambiguous "how many of those?" (no domain keyword) | intent `general`, confidence low → classifier invoked |
| C3 ＋ | classifier returns higher-confidence intent | `MergeClassifiedPlan` folds in its `Needs`; prompt reflects merged data; `WasClassifierCall(0) == true` |
| C4 ＋ | classifier throws/invalid JSON | falls back to deterministic plan gracefully, still answers |
| C5 ＋ | classifier confidence < deterministic | classifier result **ignored** (line 92 guard) |

### D. Layer 3/Phase 2 — query plan & data level
| # | Test | Asserts |
|---|---|---|
| D1 ＋ `[Theory]` | maps intent → `TransactionDataLevel`: spending_total→`AggregateOnly`, activity_count→`MatchingRows`, transaction_list/"show largest"→`MatchingRows`/ordered, comparison→summaries only | correct enum, no over-fetch |
| D2 ⊕ | aggregate "how much did I spend on food" | `recentTransactions:[]`, `detailedTransactionsIncluded:0` (exists) |
| D3 ⊕ | detail "find my coffee" | rows included, `detailedTransactionsIncluded:1` (exists) |
| D4 ⊕ | >2000 rows in a cycle | `aggregatesTruncated:true`, `aggregatesCoverAll…:false` |
| D5 ＋ | comparison across 6 cycles | summaries present, raw rows NOT dragged along |

### E. Layer 4 — deterministic calculations (unit, DB-free)
| # | Test | Asserts |
|---|---|---|
| E1 ＋ `[Theory]` | `ActivityCount(rows,"badminton",range)` | exact count; case-insensitive; matches description OR category; excludes out-of-range |
| E2 ＋ | activity count in prompt | prompt contains server `matchCount`, system instruction no longer says "count from recentTransactions" |
| E3 ＋ `[Theory]` | income/outflow/net exclude transfers (`Transfer:` prefix and `Transfer` category) | transfers excluded from all three |
| E4 ＋ | category spend / ledgerNet totals | match hand-computed values |
| E5 ＋ | wishlist forecast = **median** positive net over **complete** cycles, current partial cycle excluded | correct cycles/date; distinct from old mean behavior |
| E6 ＋ | forecast with ≤0 savings or no items | `insufficient-positive-savings-data` status |
| E7 ⊕ | forecast happy path | `estimatedCycles`, target date, `savingsPerCycle` present (exists) |

### F. Layer 5 — sufficiency validation & re-fetch (the differentiator)
| # | Test | Asserts |
|---|---|---|
| F1 ＋ | `ContextValidator.FindMissingRequirements(intent, ctx)` for activity_count missing matching rows | returns the missing requirement |
| F2 ＋ | full flow: activity_count where first plan omitted rows | **second DB fetch happens, `CallCount == 1` (no extra model call)**, final prompt contains rows |
| F3 ＋ | requirements satisfied first time | no re-fetch, no extra query (spy on query count or row provenance) |
| F4 ＋ | genuinely empty cycle (`hasTransactions:false`) | marked explicitly unavailable, NOT re-fetched forever (no infinite loop) |
| F5 ＋ | re-fetch cap | at most one re-fetch attempt per turn |

### G. Layer 6 — context shaping & sensitive mode
| # | Test | Asserts |
|---|---|---|
| G1 ⊕ | improvement question, sensitive OFF | `budgetTargets` present with alloc fractions (exists) |
| G2 ⊕ | sensitive ON | `budgetTargets` key absent; amounts stripped from rows; forecast hidden (exists + extend) |
| G3 ＋ | sensitive ON + edit command | "unhide balances" message, no edit action |
| G4 ＋ | `WhenWritingNull` | unused optional blocks absent from prompt entirely |
| G5 ⊕ | named old cycle | uses that cycle's data, not newest rows; `aggregatesCoverAll…:true` (exists) |
| G6 ⊕ | previous-cycle question | active cycle NOT mixed in (exists) |

### H. Layer 7 — structured conversation state (do last)
| # | Test | Asserts |
|---|---|---|
| H1 ＋ | turn 1 sets state (lastCycle=Jun, lastSearch=badminton) | state persisted/returned |
| H2 ＋ | turn 2 "what about the cycle before that?" | resolves to May using state, not prose re-parsing |
| H3 ＋ | turn 2 "how much did those cost?" | reuses prior matched IDs |
| H4 ＋ | state contains no amounts/records | privacy assertion — only refs/params stored |
| H5 ＋ | stale/dropped state | falls back to fresh parse, no crash |

### I. Action validation (regression — critical safety, already partly covered)
| # | Test | Asserts |
|---|---|---|
| I1 ＋ | model returns disallowed action type | filtered out |
| I2 ＋ | question-only message + model returns `openLedger` | stripped (line 1410) |
| I3 ＋ | edit action with unknown id | rejected (`HasKnownId`) |
| I4 ＋ `[Theory]` | payload validation: bad category / ledgerCategory / txType / year / amount<0 / bad ISO date | action rejected |
| I5 ＋ | sensitive mode + openEdit | dropped; amount/price stripped from payload+changes |
| I6 ＋ | > 3 actions | capped at 3 |
| I7 ＋ | `closeChat` only true for non-edit action with no follow-up question | correct flag |

### J. Provider / controller behavior
| # | Test | Asserts |
|---|---|---|
| J1 ＋ | `AiClientException` | `IsProviderError == true`; controller → 503 |
| J2 ＋ | invalid JSON from model | controller → 503 with friendly reply |
| J3 ＋ | happy path | 200; reply + validated actions |

### K. Golden corpus `[Theory]` (regression harness)
One data-driven test over ~30 rows: `(message, history) → expected {intents[], targetCycleKeys[],
dataLevel, expectedActionTypes[]}`. Includes every doc example plus the tricky ones:
"Can I afford badminton this month?", "Don't open my wishlist", "What about the last one?",
"Change that bill". **This is the primary guard against regex regressions** — CI fails if any
new pattern flips a corpus row.

---

## 5. Implementation plan (phased, incremental)

Each phase is independently shippable and keeps the corpus (§4.K) green.

### Phase 0 — Lock in current behavior (do first, before touching anything)
- Upgrade `CapturingHandler` → `ScriptedAiHandler` (§3); migrate existing tests.
- Write the golden corpus (§4.K) capturing **today's** outputs as the baseline.
- Add classifier telemetry: log `{deterministicConfidence, usedClassifier, classifierConfidence,
  finalIntents}` so you can measure whether the classifier earns its cost.
- **Done when:** corpus is green against unmodified code; telemetry visible in logs.

### Phase 1 — Finish Layer 4 (calculations), highest ROI
- Extract `BuildCycleSummaries`, `BuildWishlistForecast`, `IsTransfer`, and a **new**
  `ActivityCount` into a DB-free static `AiMetricsCalculator`. Unit-test exhaustively (§4.E).
- Add server-computed `matchCount` for activity_count; put it in the context; drop the
  "count from recentTransactions" clause from the system instruction (line 1345).
- Change wishlist forecast to median-positive-net over complete cycles, excluding the current
  partial cycle (§4.E5).
- **Done when:** E1–E7 pass; badminton count no longer depends on the model.

### Phase 2 — `AiQueryPlan` + `TransactionDataLevel`
- Introduce the record + enum. `BuildDeterministicIntentPlan`/`MergeClassifiedPlan` produce a
  **plan**; `BuildContextAsync` reads *only* the plan (retire the 7 booleans as the public
  interface). Keep `ResolveTargetCycles` as-is underneath.
- **Done when:** D1–D5 pass; `ContextNeeds` no longer crosses the planner↔context boundary;
  corpus green.

### Phase 3 — Layer 5 sufficiency + re-fetch (the differentiator)
- Add `AiIntentRequirements` (per-intent required outputs) and
  `ContextValidator.FindMissingRequirements(plan, context)`.
- In `ChatAsync`, after `BuildContextAsync`, if requirements are missing, run **one** targeted
  DB re-fetch (`ContextLoader.LoadMissingAsync`) — no extra model call. Mark genuinely-empty
  targets unavailable and stop.
- **Done when:** F1–F5 pass, especially "re-fetch with `CallCount == 1`."

### Phase 4 — Layer 7 structured state (last, privacy-guarded)
- Persist `{lastIntent, lastCycle, lastSearchText, lastMatchedTransactionIds, lastWishlistItemId}`
  — references only, never amounts. Resolve follow-ups from state before re-parsing prose.
- **Done when:** H1–H5 pass, including the no-amounts privacy assertion.

### Cross-cutting
- Every phase: `dotnet test` green (esp. the corpus), and manual e2e via `/run` + `/verify`
  for 3–4 golden cases against a seeded DB.
- Do **not** add autonomous model-driven DB tools — the doc's own conclusion, and correct for
  this compact, known domain.

---

## 6. Command cheatsheet
```
cd FinancialAppApi
dotnet test                                                  # full suite
dotnet test --filter "FullyQualifiedName~AiAssistant"        # AI tests only
dotnet test --filter "FullyQualifiedName~AiMetricsCalculator" # Layer-4 unit tests
dotnet build
```
