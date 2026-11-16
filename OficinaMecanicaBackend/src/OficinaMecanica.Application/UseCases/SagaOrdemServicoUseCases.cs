using OficinaMecanica.Application.Common;
using OficinaMecanica.Application.DTOs.OrdensServico;
using OficinaMecanica.Application.Saga;

namespace OficinaMecanica.Application.UseCases;

/// <summary>Consulta do estado da saga de uma OS (endpoint administrativo).</summary>
public class SagaOrdemServicoUseCases
{
    private readonly IConsultaSagaOs _sagas;

    public SagaOrdemServicoUseCases(IConsultaSagaOs sagas) => _sagas = sagas;

    public async Task<ServiceResult<SagaOrdemServicoDto>> ObterAsync(int osId)
    {
        var saga = await _sagas.ObterPorOsIdAsync(osId);
        if (saga is null)
            return ServiceResult<SagaOrdemServicoDto>.NotFound(
                $"Saga da OS {osId} não encontrada. A OS não existe ou o evento de abertura ainda não foi processado.");

        return ServiceResult<SagaOrdemServicoDto>.Ok(new SagaOrdemServicoDto(
            saga.CorrelationId,
            saga.CurrentState,
            saga.PrazoExpiraEm,
            saga.OrcamentoId,
            saga.PagamentoId,
            saga.LinkPagamento,
            saga.MotivoCancelamento,
            saga.CriadoEm,
            saga.AtualizadoEm));
    }
}
