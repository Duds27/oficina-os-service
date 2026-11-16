using OficinaMecanica.Application.Abstractions;
using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Application.Services;

/// <summary>
/// Devolução de peças ao estoque, usada na remoção de um item e na compensação da saga.
/// Só altera as entidades rastreadas: quem chama é responsável por salvar.
/// </summary>
public class EstoqueService
{
    private readonly IPecaRepository _pecas;

    public EstoqueService(IPecaRepository pecas) => _pecas = pecas;

    /// <summary>Devolve ao estoque a peça de um item. Itens de serviço são ignorados.</summary>
    public async Task DevolverItemAsync(ItemOrdemServico item, DateTime agora, CancellationToken ct = default)
    {
        if (item.Tipo != TipoItemOrdemServico.Peca || !item.PecaId.HasValue)
            return;

        var peca = await _pecas.ObterAsync(item.PecaId.Value, tracking: true, ct);
        peca?.DevolverAoEstoque(item.Quantidade, agora);
    }

    /// <summary>
    /// Devolve ao estoque todas as peças da OS (compensação). Idempotente: a OS guarda
    /// quando a devolução foi feita, e uma segunda chamada não faz nada.
    /// </summary>
    /// <returns><c>true</c> se devolveu agora; <c>false</c> se já tinha devolvido antes.</returns>
    public async Task<bool> DevolverPecasAsync(OrdemServico ordem, DateTime agora, CancellationToken ct = default)
    {
        if (ordem.PecasDevolvidasEm.HasValue)
            return false;

        foreach (var item in ordem.Itens)
            await DevolverItemAsync(item, agora, ct);

        ordem.PecasDevolvidasEm = agora;
        ordem.DataAtualizacao = agora;
        return true;
    }
}
