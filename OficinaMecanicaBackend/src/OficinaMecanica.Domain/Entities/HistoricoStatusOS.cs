using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Domain.Entities;

/// <summary>
/// Uma transição de status da OS. Toda mudança de status gera um registro,
/// criado por <see cref="OrdemServico.MudarStatus"/>.
/// </summary>
public class HistoricoStatusOS
{
    public int Id { get; set; }
    public int OrdemServicoId { get; set; }
    public StatusOrdemServico StatusAnterior { get; set; }
    public StatusOrdemServico StatusNovo { get; set; }
    public OrigemMudancaStatus Origem { get; set; }
    public string? Motivo { get; set; }

    /// <summary>Id da saga da OS no momento da transição.</summary>
    public Guid CorrelationId { get; set; }

    public DateTime OcorridoEm { get; set; }

    public OrdemServico OrdemServico { get; set; } = null!;
}
