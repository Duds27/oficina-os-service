namespace OficinaMecanica.Application.Saga;

/// <summary>Leitura do estado da saga de uma OS (endpoint administrativo e cancelamento).</summary>
public interface IConsultaSagaOs
{
    Task<OrdemServicoSagaState?> ObterPorOsIdAsync(int osId, CancellationToken ct = default);
}
