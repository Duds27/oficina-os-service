using MassTransit;

namespace OficinaMecanica.Application.Saga;

/// <summary>
/// Estado persistido da saga de uma OS (tabela SagaOrdemServico). A instância não é
/// apagada ao final: fica consultável em GET /api/ordens-servico/{id}/saga.
/// </summary>
public class OrdemServicoSagaState : SagaStateMachineInstance
{
    /// <summary>Id da saga, igual a <c>OrdemServico.CorrelationId</c>.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>Nome do estado atual (ex.: "AguardandoAprovacao").</summary>
    public string CurrentState { get; set; } = string.Empty;

    public int OsId { get; set; }
    public string NumeroOS { get; set; } = string.Empty;

    public Guid? OrcamentoId { get; set; }
    public Guid? PagamentoId { get; set; }
    public string? LinkPagamento { get; set; }

    /// <summary>
    /// Prazo do estado de espera atual (aprovação, pagamento ou execução). O
    /// VerificadorPrazosSaga publica PrazoExpirado quando ele vence e limpa o campo.
    /// </summary>
    public DateTime? PrazoExpiraEm { get; set; }

    /// <summary>Por que a saga foi cancelada (recusa, prazo, rejeição...).</summary>
    public string? MotivoCancelamento { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime? AtualizadoEm { get; set; }

    /// <summary>
    /// Desde quando a saga está no estado atual. Alimenta a métrica <c>saga.etapa.duracao</c>.
    /// Diferente de <see cref="AtualizadoEm"/>, que muda a cada evento tratado.
    /// </summary>
    public DateTime? EstadoDesde { get; set; }

    /// <summary>
    /// Concorrência otimista. No MySQL, o Pomelo mapeia DateTime + IsRowVersion() para
    /// timestamp(6) com ON UPDATE CURRENT_TIMESTAMP(6): o banco troca o valor a cada UPDATE.
    /// </summary>
    public DateTime RowVersion { get; set; }
}
