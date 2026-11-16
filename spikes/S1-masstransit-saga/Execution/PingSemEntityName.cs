using MassTransit;

namespace Spike.Execution.Contracts.SemEntityName;

/// <summary>Mesmo URN do Ping do os-service, sem EntityName, em outro namespace.</summary>
[MessageUrn("oficina:ping-sem-entity-name:v1")]
public record Ping
{
    public Guid Id { get; init; }
}
