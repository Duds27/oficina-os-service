using MassTransit;

// Contratos do lado do os-service. O execution-service declara as mesmas mensagens em
// outro namespace (Spike.Execution.Contracts), como cada repo fará na Fase 4.
namespace Spike.Os.Contracts;

[ExcludeFromTopology]
public interface IMensagemOS
{
    Guid CorrelationId { get; }
    int OsId { get; }
    string NumeroOS { get; }
    DateTime OcorridoEm { get; }
}

[ExcludeFromTopology]
public abstract record MensagemOS : IMensagemOS
{
    public Guid CorrelationId { get; init; }
    public int OsId { get; init; }
    public string NumeroOS { get; init; } = string.Empty;
    public DateTime OcorridoEm { get; init; }
}

public record VeiculoInfo
{
    public string Placa { get; init; } = string.Empty;
    public string Marca { get; init; } = string.Empty;
    public string Modelo { get; init; } = string.Empty;
    public int Ano { get; init; }
}

[MessageUrn("oficina:ordem-servico-aberta:v1"), EntityName("oficina.ordem-servico-aberta.v1")]
public record OrdemServicoAberta : MensagemOS;

[MessageUrn("oficina:solicitar-diagnostico:v1"), EntityName("oficina.solicitar-diagnostico.v1")]
public record SolicitarDiagnostico : MensagemOS
{
    public VeiculoInfo Veiculo { get; init; } = new();
    public string? Descricao { get; init; }
}

[MessageUrn("oficina:diagnostico-iniciado:v1"), EntityName("oficina.diagnostico-iniciado.v1")]
public record DiagnosticoIniciado : MensagemOS
{
    public string Mecanico { get; init; } = string.Empty;
}
