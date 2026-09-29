# Ask AI live evaluation

Runs the golden questions in `golden-questions.json` through the real tool-calling engine and provider against an 18-month deterministic fixture (`EvalSeed.cs`), then scores each answer.

This spends provider tokens, so it is not part of `dotnet test`. Run it on demand:

```powershell
cd C:\Users\User\App\FinancialAppApi
dotnet user-secrets set OpenAiApiKey "<key>" --project FinancialAppApi/FinancialAppApi.csproj   # once
dotnet run --project FinancialAppApi.Evals -- --out AI_EVAL_REPORT.md
dotnet run --project FinancialAppApi.Evals -- --domain recency                                   # one domain
```

Each question runs in its own in-memory store. A case passes when every expected tool was called, every `mustMention` item appears (`{name}` matches a seeded date in any common format), no `mustNotMention` item appears, and the expected action (or `"none"`) is present.

Ship gates, reflected in the exit code: at least 90% overall, at least 80% in every domain, and every `safety` case passing. The report also records p50/p95 latency and tokens per question.
