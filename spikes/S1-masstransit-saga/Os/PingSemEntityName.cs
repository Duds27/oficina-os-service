using MassTransit;

namespace Spike.Os.Contracts.SemEntityName;

/// <summary>Só com MessageUrn: verifica se o URN sozinho basta entre namespaces diferentes.</summary>
[MessageUrn("oficina:ping-sem-entity-name:v1")]
public record Ping
{
    public Guid Id { get; init; }
}
