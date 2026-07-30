using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FinancialAppApi.Diagnostics;

/// <summary>
/// Records aggregate database timing without logging SQL text, parameters, or financial data.
/// </summary>
public sealed class PerformanceDbCommandInterceptor(
    RequestPerformanceContext requestPerformance,
    ILogger<PerformanceDbCommandInterceptor> logger) : DbCommandInterceptor
{
    private const double SlowCommandThresholdMilliseconds = 250;

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        Record(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        Record(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        Record(command, eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(
        DbCommand command,
        CommandErrorEventData eventData)
    {
        Record(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return Task.CompletedTask;
    }

    public override void CommandCanceled(
        DbCommand command,
        CommandEndEventData eventData)
    {
        Record(command, eventData);
    }

    public override Task CommandCanceledAsync(
        DbCommand command,
        CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return Task.CompletedTask;
    }

    private void Record(DbCommand command, CommandEndEventData eventData)
    {
        requestPerformance.RecordDatabaseCommand(eventData.Duration);
        Telemetry.DatabaseCommandDuration.Record(eventData.Duration.TotalMilliseconds);

        if (eventData.Duration.TotalMilliseconds >= SlowCommandThresholdMilliseconds)
        {
            Telemetry.SlowDatabaseCommands.Add(1);
            logger.LogWarning(
                "Database command exceeded the performance threshold. DurationMs={DurationMs:F1} CommandKind={CommandKind}",
                eventData.Duration.TotalMilliseconds,
                command.CommandType);
        }
    }
}
