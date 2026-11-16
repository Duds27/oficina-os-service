using Microsoft.Extensions.Logging;
using Oficina.Contracts;
using OficinaMecanica.Application.Abstractions;
using OficinaMecanica.Application.Common;
using OficinaMecanica.Application.DTOs.OrdensServico;
using OficinaMecanica.Application.Services;
using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Application.UseCases;

public class OrdemServicoUseCases
{
    private readonly IOrdemServicoRepository _ordens;
    private readonly IClienteRepository _clientes;
    private readonly IVeiculoRepository _veiculos;
    private readonly IPecaRepository _pecas;
    private readonly IServicoRepository _servicos;
    private readonly IUnitOfWork _uow;
    private readonly INotificadorStatus _notificador;
    private readonly EstoqueService _estoque;
    private readonly IPublicadorEventos _publicador;
    private readonly ILogger<OrdemServicoUseCases> _logger;

    public OrdemServicoUseCases(
        IOrdemServicoRepository ordens,
        IClienteRepository clientes,
        IVeiculoRepository veiculos,
        IPecaRepository pecas,
        IServicoRepository servicos,
        IUnitOfWork uow,
        INotificadorStatus notificador,
        EstoqueService estoque,
        IPublicadorEventos publicador,
        ILogger<OrdemServicoUseCases> logger)
    {
        _ordens = ordens;
        _clientes = clientes;
        _veiculos = veiculos;
        _pecas = pecas;
        _servicos = servicos;
        _uow = uow;
        _notificador = notificador;
        _estoque = estoque;
        _publicador = publicador;
        _logger = logger;
    }

    /// <summary>
    /// Lista as OS ativas ordenadas por prioridade de status
    /// (Em Execução &gt; Aguardando Aprovação &gt; Em Diagnóstico &gt; Recebida) e,
    /// dentro do mesmo status, das mais antigas para as mais recentes. OS
    /// finalizadas e entregues são omitidas (exclusão lógica), salvo se
    /// <paramref name="incluirConcluidas"/> for verdadeiro.
    /// </summary>
    public async Task<ServiceResult<IEnumerable<OrdemServicoDto>>> ListarAsync(bool incluirConcluidas = false)
    {
        var ordens = await _ordens.ListarComIncludesAsync();

        var filtradas = incluirConcluidas
            ? ordens
            : ordens.Where(o => o.VisivelNaListagem);

        var ordenadas = filtradas
            .OrderBy(o => OrdemServico.PrioridadeListagem(o.Status))
            .ThenBy(o => o.DataCriacao)
            .Select(ToDto);

        return ServiceResult<IEnumerable<OrdemServicoDto>>.Ok(ordenadas);
    }

    public async Task<ServiceResult<OrdemServicoDto>> ObterAsync(int id)
    {
        var ordem = await _ordens.ObterComIncludesAsync(id, tracking: false);
        if (ordem is null)
            return ServiceResult<OrdemServicoDto>.NotFound($"Ordem de serviço {id} não encontrada.");
        return ServiceResult<OrdemServicoDto>.Ok(ToDto(ordem));
    }

    public async Task<ServiceResult<StatusPublicoDto>> ObterStatusPublicoAsync(int id)
    {
        var ordem = await _ordens.ObterAsync(id);
        if (ordem is null)
            return ServiceResult<StatusPublicoDto>.NotFound($"Ordem de serviço {id} não encontrada.");

        return ServiceResult<StatusPublicoDto>.Ok(new StatusPublicoDto(
            ordem.NumeroOS,
            ordem.Status,
            ordem.Status.ToString(),
            ordem.DataPrevisaoTermino
        ));
    }

    public async Task<ServiceResult<TempoMedioExecucaoDto>> CalcularTempoMedioExecucaoAsync()
    {
        var ordens = await _ordens.ListarFinalizadasComExecucaoAsync();

        if (ordens.Count == 0)
            return ServiceResult<TempoMedioExecucaoDto>.Ok(
                new TempoMedioExecucaoDto(0, 0, 0, 0, "00:00:00"));

        var mediaSegundos = ordens.Average(o =>
            (o.DataFinalizacao!.Value - o.DataInicioExecucao!.Value).TotalSeconds);
        var media = TimeSpan.FromSeconds(mediaSegundos);

        return ServiceResult<TempoMedioExecucaoDto>.Ok(new TempoMedioExecucaoDto(
            ordens.Count,
            Math.Round(mediaSegundos, 2),
            Math.Round(media.TotalMinutes, 2),
            Math.Round(media.TotalHours, 2),
            $"{(int)media.TotalHours:D2}:{media.Minutes:D2}:{media.Seconds:D2}"
        ));
    }

    public async Task<ServiceResult<OrdemServicoDto>> CriarAsync(CreateOrdemServicoDto dto)
    {
        if (!await _clientes.ExisteAsync(dto.ClienteId))
            return ServiceResult<OrdemServicoDto>.NotFound($"Cliente {dto.ClienteId} não encontrado.");

        if (!await _veiculos.PertenceAoClienteAsync(dto.VeiculoId, dto.ClienteId))
            return ServiceResult<OrdemServicoDto>.NotFound($"Veículo {dto.VeiculoId} não encontrado ou não pertence ao cliente.");

        var ordem = new OrdemServico
        {
            CorrelationId = Guid.NewGuid(),
            ClienteId = dto.ClienteId,
            VeiculoId = dto.VeiculoId,
            Status = StatusOrdemServico.Recebida,
            Descricao = dto.Descricao,
            ValorTotal = 0,
            OrcamentoAprovado = false,
            DataPrevisaoTermino = dto.DataPrevisaoTermino,
            DataCriacao = DateTime.UtcNow
        };
        await _ordens.AdicionarAsync(ordem);

        // A OS e o evento que inicia a saga são gravados na mesma transação (outbox):
        // ou os dois existem, ou nenhum. O evento precisa do Id da OS, então a OS é
        // salva primeiro, dentro da transação.
        const int maxTentativas = 2;
        for (var tentativa = 1; ; tentativa++)
        {
            await using var tx = await _uow.IniciarTransacaoAsync();
            ordem.NumeroOS = await GerarNumeroOSAsync();
            try
            {
                await _uow.SalvarAsync();
            }
            catch (ConflitoPersistenciaException) when (tentativa < maxTentativas)
            {
                // Outra OS pegou o mesmo NumeroOS (condição de corrida). Desfaz e tenta de
                // novo numa transação nova, que já enxerga o número que a outra gravou.
                await tx.RollbackAsync();
                continue;
            }

            await _publicador.PublicarAsync(new OrdemServicoAberta
            {
                CorrelationId = ordem.CorrelationId,
                OsId = ordem.Id,
                NumeroOS = ordem.NumeroOS,
                OcorridoEm = ordem.DataCriacao
            });
            await _uow.SalvarAsync();

            await tx.CommitAsync();
            break;
        }

        // Evento de negócio estruturado (base dos dashboards de observabilidade:
        // volume diário de OS). O CorrelationId é adicionado pelo enricher do Serilog.
        _logger.LogInformation(
            "Ordem de serviço criada. Evento={Evento} NumeroOS={NumeroOS} ClienteId={ClienteId} VeiculoId={VeiculoId}",
            "os_criada", ordem.NumeroOS, ordem.ClienteId, ordem.VeiculoId);

        var ordemComIncludes = await _ordens.ObterComIncludesAsync(ordem.Id, tracking: false);
        return ServiceResult<OrdemServicoDto>.Created(ToDto(ordemComIncludes!));
    }

    /// <summary>
    /// Entrega da OS ao cliente (Finalizada → Entregue). É a única mudança de status feita
    /// por usuário: as outras vêm da saga, em resposta aos outros serviços.
    /// </summary>
    public async Task<ServiceResult<OrdemServicoDto>> EntregarAsync(int id)
    {
        var ordem = await _ordens.ObterComIncludesAsync(id, tracking: true);
        if (ordem is null)
            return ServiceResult<OrdemServicoDto>.NotFound($"Ordem de serviço {id} não encontrada.");

        if (ordem.Status != StatusOrdemServico.Finalizada)
            return ServiceResult<OrdemServicoDto>.BadRequest(
                $"Só é possível entregar uma OS finalizada. Status atual: {ordem.Status}.");

        var statusAnterior = ordem.Status;
        ordem.MudarStatus(StatusOrdemServico.Entregue, OrigemMudancaStatus.Usuario, "Entregue ao cliente", DateTime.UtcNow);

        try
        {
            await _notificador.NotificarMudancaStatusAsync(ordem, statusAnterior, StatusOrdemServico.Entregue);
        }
        catch (Exception ex)
        {
            // Falha no processamento/notificação da OS — evento base do alerta de observabilidade.
            _logger.LogError(ex,
                "Falha ao processar notificação da OS. Evento={Evento} NumeroOS={NumeroOS} StatusNovo={StatusNovo}",
                "os_falha_processamento", ordem.NumeroOS, StatusOrdemServico.Entregue.ToString());
            throw;
        }

        // Status, histórico e notificação são gravados juntos.
        await _uow.SalvarAsync();

        // Evento de negócio estruturado: transições nos dashboards.
        _logger.LogInformation(
            "Status da OS alterado. Evento={Evento} NumeroOS={NumeroOS} StatusAnterior={StatusAnterior} StatusNovo={StatusNovo} Origem={Origem}",
            "os_status_alterado", ordem.NumeroOS, statusAnterior.ToString(), StatusOrdemServico.Entregue.ToString(),
            OrigemMudancaStatus.Usuario.ToString());

        return ServiceResult<OrdemServicoDto>.Ok(ToDto(ordem));
    }

    /// <summary>Transições de status da OS, da mais antiga para a mais recente.</summary>
    public async Task<ServiceResult<IEnumerable<HistoricoStatusDto>>> ListarHistoricoAsync(int id)
    {
        if (await _ordens.ObterAsync(id) is null)
            return ServiceResult<IEnumerable<HistoricoStatusDto>>.NotFound($"Ordem de serviço {id} não encontrada.");

        var historico = await _ordens.ListarHistoricoAsync(id);
        return ServiceResult<IEnumerable<HistoricoStatusDto>>.Ok(historico.Select(h => new HistoricoStatusDto(
            h.StatusAnterior.ToString(),
            h.StatusNovo.ToString(),
            h.Origem.ToString(),
            h.Motivo,
            h.OcorridoEm)));
    }

    /// <summary>
    /// Pede o cancelamento da OS. Só publica o evento (pelo outbox): a saga aplica a
    /// compensação do estado em que estiver (docs/contratos-mensagens.md, seção 4).
    /// </summary>
    public async Task<ServiceResult<CancelamentoSolicitadoDto>> CancelarAsync(int id, string? motivo, string solicitadoPor)
    {
        var ordem = await _ordens.ObterAsync(id);
        if (ordem is null)
            return ServiceResult<CancelamentoSolicitadoDto>.NotFound($"Ordem de serviço {id} não encontrada.");

        if (ordem.Encerrada)
            return ServiceResult<CancelamentoSolicitadoDto>.Conflict(
                $"A OS já está encerrada ({ordem.Status}) e não pode ser cancelada.");

        if (ordem.Status == StatusOrdemServico.EmExecucao)
            return ServiceResult<CancelamentoSolicitadoDto>.Conflict(
                "A OS está em execução e não pode mais ser cancelada.");

        var motivoFinal = string.IsNullOrWhiteSpace(motivo) ? "Cancelamento solicitado" : motivo.Trim();
        await _publicador.PublicarAsync(new CancelamentoSolicitado
        {
            CorrelationId = ordem.CorrelationId,
            OsId = ordem.Id,
            NumeroOS = ordem.NumeroOS,
            OcorridoEm = DateTime.UtcNow,
            Motivo = motivoFinal,
            SolicitadoPor = solicitadoPor
        });
        await _uow.SalvarAsync();

        _logger.LogInformation(
            "Cancelamento da OS solicitado. Evento={Evento} NumeroOS={NumeroOS} SolicitadoPor={SolicitadoPor}",
            "os_cancelamento_solicitado", ordem.NumeroOS, solicitadoPor);

        return ServiceResult<CancelamentoSolicitadoDto>.Ok(
            new CancelamentoSolicitadoDto(ordem.Id, ordem.NumeroOS, ordem.CorrelationId, motivoFinal));
    }

    public async Task<ServiceResult<OrdemServicoDto>> AdicionarItemAsync(int ordemServicoId, AddItemDto dto)
    {
        await using var tx = await _uow.IniciarTransacaoAsync();
        try
        {
            var ordem = await _ordens.ObterComIncludesAsync(ordemServicoId, tracking: true);
            if (ordem is null)
                return ServiceResult<OrdemServicoDto>.NotFound($"Ordem de serviço {ordemServicoId} não encontrada.");

            if (!ordem.PermiteAlterarItens)
                return ServiceResult<OrdemServicoDto>.BadRequest(
                    $"Itens só podem ser adicionados durante o diagnóstico (Recebida ou EmDiagnostico). Status atual: {ordem.Status}.");

            decimal precoUnitario;

            if (dto.Tipo == TipoItemOrdemServico.Servico)
            {
                var servico = await _servicos.ObterAsync(dto.ServicoId!.Value, tracking: true);
                if (servico is null)
                    return ServiceResult<OrdemServicoDto>.NotFound($"Serviço {dto.ServicoId} não encontrado.");
                precoUnitario = servico.PrecoBase;
            }
            else
            {
                var peca = await _pecas.ObterAsync(dto.PecaId!.Value, tracking: true);
                if (peca is null)
                    return ServiceResult<OrdemServicoDto>.NotFound($"Peça {dto.PecaId} não encontrada.");

                if (peca.QuantidadeEstoque < (int)dto.Quantidade)
                    return ServiceResult<OrdemServicoDto>.BadRequest(
                        $"Estoque insuficiente para a peça '{peca.Nome}'. Disponível: {peca.QuantidadeEstoque}, solicitado: {dto.Quantidade}.");

                peca.QuantidadeEstoque -= (int)dto.Quantidade;
                peca.DataAtualizacao = DateTime.UtcNow;

                if (peca.QuantidadeEstoque < peca.QuantidadeMinima)
                    _logger.LogWarning("Peça {PecaId} ({Nome}) abaixo do estoque mínimo: {Atual}/{Minimo}",
                        peca.Id, peca.Nome, peca.QuantidadeEstoque, peca.QuantidadeMinima);

                precoUnitario = peca.PrecoUnitario;
            }

            var item = new ItemOrdemServico
            {
                OrdemServicoId = ordemServicoId,
                Tipo = dto.Tipo,
                ServicoId = dto.Tipo == TipoItemOrdemServico.Servico ? dto.ServicoId : null,
                PecaId = dto.Tipo == TipoItemOrdemServico.Peca ? dto.PecaId : null,
                Quantidade = dto.Quantidade,
                PrecoUnitario = precoUnitario,
                PrecoTotal = precoUnitario * dto.Quantidade,
                DataCriacao = DateTime.UtcNow
            };

            // Capture existing total BEFORE Add() so o change tracker não conte em dobro
            var existingTotal = ordem.Itens.Sum(i => i.PrecoTotal);
            await _ordens.AdicionarItemAsync(item);

            ordem.ValorTotal = existingTotal + item.PrecoTotal;
            ordem.DataAtualizacao = DateTime.UtcNow;

            await _uow.SalvarAsync();
            await tx.CommitAsync();

            var ordemAtualizada = await _ordens.ObterComIncludesAsync(ordemServicoId, tracking: false);
            return ServiceResult<OrdemServicoDto>.Created(ToDto(ordemAtualizada!));
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<ServiceResult<OrdemServicoDto>> RemoverItemAsync(int ordemServicoId, int itemId)
    {
        await using var tx = await _uow.IniciarTransacaoAsync();
        try
        {
            var ordem = await _ordens.ObterComIncludesAsync(ordemServicoId, tracking: true);
            if (ordem is null)
                return ServiceResult<OrdemServicoDto>.NotFound($"Ordem de serviço {ordemServicoId} não encontrada.");

            var item = ordem.Itens.FirstOrDefault(i => i.Id == itemId);
            if (item is null)
                return ServiceResult<OrdemServicoDto>.NotFound($"Item {itemId} não encontrado na OS {ordemServicoId}.");

            if (!ordem.PermiteAlterarItens)
                return ServiceResult<OrdemServicoDto>.BadRequest(
                    $"Itens só podem ser removidos durante o diagnóstico (Recebida ou EmDiagnostico). Status atual: {ordem.Status}.");

            await _estoque.DevolverItemAsync(item, DateTime.UtcNow);

            _ordens.RemoverItem(item);

            ordem.ValorTotal = ordem.Itens
                .Where(i => i.Id != itemId)
                .Sum(i => i.PrecoTotal);
            ordem.DataAtualizacao = DateTime.UtcNow;

            await _uow.SalvarAsync();
            await tx.CommitAsync();

            var ordemAtualizada = await _ordens.ObterComIncludesAsync(ordemServicoId, tracking: false);
            return ServiceResult<OrdemServicoDto>.Ok(ToDto(ordemAtualizada!));
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private async Task<string> GerarNumeroOSAsync()
    {
        var ano = DateTime.UtcNow.Year;
        var prefixo = $"OS-{ano}-";

        var ultimo = await _ordens.UltimoNumeroOSAsync(prefixo);

        int seq = 1;
        if (ultimo is not null)
        {
            var partes = ultimo.Split('-');
            if (partes.Length == 3 && int.TryParse(partes[2], out var ultimoSeq))
                seq = ultimoSeq + 1;
        }

        return $"{prefixo}{seq:D6}";
    }

    private static OrdemServicoDto ToDto(OrdemServico o) => new(
        o.Id,
        o.NumeroOS,
        o.ClienteId,
        o.Cliente?.Nome ?? string.Empty,
        o.VeiculoId,
        o.Veiculo?.Placa ?? string.Empty,
        o.Status,
        o.Descricao,
        o.ValorTotal,
        o.OrcamentoAprovado,
        o.DataAprovacaoOrcamento,
        o.DataPrevisaoTermino,
        o.DataInicioExecucao,
        o.DataFinalizacao,
        o.DataEntrega,
        o.DataCriacao,
        o.DataAtualizacao,
        o.Itens.Select(i => new ItemOrdemServicoDto(
            i.Id,
            i.Tipo,
            i.ServicoId,
            i.Servico?.Nome,
            i.PecaId,
            i.Peca?.Nome,
            i.Quantidade,
            i.PrecoUnitario,
            i.PrecoTotal,
            i.DataCriacao
        ))
    );
}
