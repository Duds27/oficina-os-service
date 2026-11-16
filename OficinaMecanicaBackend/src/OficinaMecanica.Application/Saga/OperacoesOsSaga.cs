using Microsoft.Extensions.Logging;
using Oficina.Contracts;
using OficinaMecanica.Application.Abstractions;
using OficinaMecanica.Application.Services;
using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Application.Saga;

public class OperacoesOsSaga : IOperacoesOsSaga
{
    private readonly IOrdemServicoRepository _ordens;
    private readonly EstoqueService _estoque;
    private readonly INotificadorStatus _notificador;
    private readonly ILogger<OperacoesOsSaga> _logger;

    public OperacoesOsSaga(
        IOrdemServicoRepository ordens,
        EstoqueService estoque,
        INotificadorStatus notificador,
        ILogger<OperacoesOsSaga> logger)
    {
        _ordens = ordens;
        _estoque = estoque;
        _notificador = notificador;
        _logger = logger;
    }

    public async Task<DadosOsSaga> ObterDadosAsync(int osId, CancellationToken ct = default)
    {
        var os = await CarregarAsync(osId, ct);

        var itens = os.Itens
            .Select(i => new ItemOrcamentoInfo
            {
                Tipo = i.Tipo == TipoItemOrdemServico.Peca ? "Peca" : "Servico",
                ReferenciaId = (i.Tipo == TipoItemOrdemServico.Peca ? i.PecaId : i.ServicoId) ?? 0,
                Descricao = (i.Tipo == TipoItemOrdemServico.Peca ? i.Peca?.Nome : i.Servico?.Nome) ?? $"Item {i.Id}",
                Quantidade = i.Quantidade,
                PrecoUnitario = i.PrecoUnitario
            })
            .ToList();

        var servicos = os.Itens
            .Where(i => i.Tipo == TipoItemOrdemServico.Servico)
            .Select(i => new ServicoExecucaoInfo
            {
                Descricao = i.Servico?.Nome ?? $"Serviço {i.ServicoId}",
                Quantidade = i.Quantidade
            })
            .ToList();

        return new DadosOsSaga(
            os.Id,
            os.NumeroOS,
            os.Descricao,
            new VeiculoInfo { Placa = os.Veiculo.Placa, Marca = os.Veiculo.Marca, Modelo = os.Veiculo.Modelo, Ano = os.Veiculo.Ano },
            new ClienteInfo { Id = os.Cliente.Id, Nome = os.Cliente.Nome, Email = os.Cliente.Email },
            itens,
            os.ValorTotal,
            servicos);
    }

    public async Task AplicarStatusAsync(int osId, StatusOrdemServico alvo, string motivo, CancellationToken ct = default)
    {
        var os = await CarregarAsync(osId, ct);

        var registros = os.AvancarAte(alvo, OrigemMudancaStatus.Saga, motivo, DateTime.UtcNow);
        foreach (var registro in registros)
        {
            await _notificador.NotificarMudancaStatusAsync(os, registro.StatusAnterior, registro.StatusNovo, ct);

            // Mesmo evento de negócio do caso de uso manual (dashboards de transições).
            _logger.LogInformation(
                "Status da OS alterado. Evento={Evento} NumeroOS={NumeroOS} StatusAnterior={StatusAnterior} StatusNovo={StatusNovo} Origem={Origem} Motivo={Motivo}",
                "os_status_alterado", os.NumeroOS, registro.StatusAnterior.ToString(), registro.StatusNovo.ToString(),
                registro.Origem.ToString(), motivo);
        }
    }

    public async Task DevolverPecasAsync(int osId, CancellationToken ct = default)
    {
        var os = await CarregarAsync(osId, ct);

        if (await _estoque.DevolverPecasAsync(os, DateTime.UtcNow, ct))
            _logger.LogInformation(
                "Peças da OS devolvidas ao estoque. Evento={Evento} NumeroOS={NumeroOS}",
                "os_pecas_devolvidas", os.NumeroOS);
    }

    private async Task<OrdemServico> CarregarAsync(int osId, CancellationToken ct) =>
        await _ordens.ObterComIncludesAsync(osId, tracking: true, ct)
        ?? throw new InvalidOperationException($"OS {osId} não encontrada para a saga.");
}
