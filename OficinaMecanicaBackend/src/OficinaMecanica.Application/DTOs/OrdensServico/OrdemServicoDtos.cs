using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Application.DTOs.OrdensServico;

public record CreateOrdemServicoDto(
    int ClienteId,
    int VeiculoId,
    string? Descricao,
    DateTime? DataPrevisaoTermino
);

/// <summary>Pedido de cancelamento da OS. A saga decide as compensações pelo estado atual.</summary>
public record CancelarOrdemServicoDto(string? Motivo);

public record AddItemDto(
    TipoItemOrdemServico Tipo,
    int? ServicoId,
    int? PecaId,
    decimal Quantidade
);

public record ItemOrdemServicoDto(
    int Id,
    TipoItemOrdemServico Tipo,
    int? ServicoId,
    string? ServicoNome,
    int? PecaId,
    string? PecaNome,
    decimal Quantidade,
    decimal PrecoUnitario,
    decimal PrecoTotal,
    DateTime DataCriacao
);

public record OrdemServicoDto(
    int Id,
    string NumeroOS,
    int ClienteId,
    string ClienteNome,
    int VeiculoId,
    string VeiculoPlaca,
    StatusOrdemServico Status,
    string? Descricao,
    decimal ValorTotal,
    bool OrcamentoAprovado,
    DateTime? DataAprovacaoOrcamento,
    DateTime? DataPrevisaoTermino,
    DateTime? DataInicioExecucao,
    DateTime? DataFinalizacao,
    DateTime? DataEntrega,
    DateTime DataCriacao,
    DateTime? DataAtualizacao,
    IEnumerable<ItemOrdemServicoDto> Itens
);

public record StatusPublicoDto(
    string NumeroOS,
    StatusOrdemServico Status,
    string StatusDescricao,
    DateTime? DataPrevisaoTermino
);

/// <summary>
/// Tempo médio de execução das ordens de serviço, medido do início da
/// execução (DataInicioExecucao) até a finalização (DataFinalizacao). Apenas
/// ordens que já iniciaram a execução e foram finalizadas entram no cálculo.
/// </summary>
public record TempoMedioExecucaoDto(
    int OrdensConsideradas,
    double TempoMedioSegundos,
    double TempoMedioMinutos,
    double TempoMedioHoras,
    string TempoMedioFormatado
);

/// <summary>Uma transição de status da OS, do histórico.</summary>
public record HistoricoStatusDto(
    string StatusAnterior,
    string StatusNovo,
    string Origem,
    string? Motivo,
    DateTime OcorridoEm
);

/// <summary>Estado da saga da OS (endpoint administrativo).</summary>
public record SagaOrdemServicoDto(
    Guid CorrelationId,
    string Estado,
    DateTime? PrazoExpiraEm,
    Guid? OrcamentoId,
    Guid? PagamentoId,
    string? LinkPagamento,
    string? MotivoCancelamento,
    DateTime CriadoEm,
    DateTime? AtualizadoEm
);

/// <summary>Resposta do pedido de cancelamento (202): a saga processa em seguida.</summary>
public record CancelamentoSolicitadoDto(int Id, string NumeroOS, Guid CorrelationId, string Motivo);
