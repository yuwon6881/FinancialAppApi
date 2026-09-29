namespace FinancialAppApi.Services.AI.Agent;

// The tool-calling engine's collaborators, injected into AiAssistantService as one dependency.
public sealed class AiAgentServices
{
    public AiAgentServices(
        AiAgentEngine engine,
        AiToolContextFactory contexts,
        AiBaselineSnapshotBuilder snapshot,
        AiUsageMeter usage)
    {
        Engine = engine;
        Contexts = contexts;
        Snapshot = snapshot;
        Usage = usage;
    }

    public AiAgentEngine Engine { get; }
    public AiToolContextFactory Contexts { get; }
    public AiBaselineSnapshotBuilder Snapshot { get; }
    public AiUsageMeter Usage { get; }
}
