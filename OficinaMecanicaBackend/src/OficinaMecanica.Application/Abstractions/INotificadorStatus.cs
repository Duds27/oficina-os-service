using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Application.Abstractions;

/// <summary>
/// Porta de saída para notificar o cliente sobre a mudança de status da OS
/// (ex.: por e-mail). A implementação de infraestrutura decide o meio real.
/// Não salva nada sozinha: o registro entra na mesma unidade de trabalho de quem
/// chama (caso de uso ou saga), que persiste tudo de uma vez.
/// </summary>
public interface INotificadorStatus
{
    Task NotificarMudancaStatusAsync(
        OrdemServico ordem,
        StatusOrdemServico statusAnterior,
        StatusOrdemServico statusNovo,
        CancellationToken ct = default);
}
