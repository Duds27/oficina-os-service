using Oficina.Contracts;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Application.Saga;

/// <summary>
/// O que a saga faz na OS. As operações só alteram entidades rastreadas no contexto da
/// mensagem: o repositório da saga salva tudo junto (estado da saga, OS, histórico,
/// estoque, notificações e mensagens de saída) na mesma transação.
/// </summary>
public interface IOperacoesOsSaga
{
    /// <summary>Dados da OS para montar os comandos aos outros serviços.</summary>
    Task<DadosOsSaga> ObterDadosAsync(int osId, CancellationToken ct = default);

    /// <summary>
    /// Leva a OS até <paramref name="alvo"/>, passando pelos status intermediários quando
    /// um evento chegou fora de ordem. Grava o histórico (origem Saga) e notifica o cliente.
    /// Não faz nada se a OS já está no alvo.
    /// </summary>
    Task AplicarStatusAsync(int osId, StatusOrdemServico alvo, string motivo, CancellationToken ct = default);

    /// <summary>Devolve as peças da OS ao estoque (compensação). Idempotente.</summary>
    Task DevolverPecasAsync(int osId, CancellationToken ct = default);
}

public record DadosOsSaga(
    int OsId,
    string NumeroOS,
    string? Descricao,
    VeiculoInfo Veiculo,
    ClienteInfo Cliente,
    IReadOnlyList<ItemOrcamentoInfo> Itens,
    decimal ValorTotal,
    IReadOnlyList<ServicoExecucaoInfo> Servicos);
