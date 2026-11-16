using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oficina.Contracts;
using OficinaMecanica.Application.DTOs.OrdensServico;
using OficinaMecanica.Application.UseCases;
using OficinaMecanica.BddTests.Suporte;
using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Infrastructure.Data;
using Reqnroll;

namespace OficinaMecanica.BddTests.Steps;

[Binding]
public class CicloDeVidaDaOsSteps
{
    private AmbienteSaga _ambiente = null!;
    private readonly Dictionary<string, int> _pecas = new();
    private int _servicoId;
    private int _osId;
    private string _numeroOS = string.Empty;
    private Guid _correlationId;
    private readonly Guid _pagamentoId = Guid.NewGuid();

    [BeforeScenario]
    public async Task IniciarAsync() => _ambiente = await AmbienteSaga.CriarAsync();

    [AfterScenario]
    public async Task EncerrarAsync() => await _ambiente.DisposeAsync();

    // ───────────── Dado ─────────────

    [Given("a peça {string} com {int} unidades em estoque")]
    public Task DadoAPecaEmEstoque(string nome, int estoque) =>
        _ambiente.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var peca = new Peca { Nome = nome, PrecoUnitario = 45.90m, QuantidadeEstoque = estoque, QuantidadeMinima = 1, DataCriacao = DateTime.UtcNow };
            var servico = new Servico { Nome = "Troca de óleo", PrecoBase = 120m, DataCriacao = DateTime.UtcNow };
            db.AddRange(peca, servico);
            await db.SaveChangesAsync();
            _pecas[nome] = peca.Id;
            _servicoId = servico.Id;
        });

    [Given("uma OS aberta para um cliente com veículo cadastrado")]
    public async Task DadoUmaOsAberta()
    {
        await _ambiente.NoEscopoAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var cliente = new Cliente { CpfCnpj = "52998224725", Nome = "Maria Souza", Email = "maria@example.com", DataCriacao = DateTime.UtcNow };
            var veiculo = new Veiculo { Placa = "ABC1D23", Marca = "Ford", Modelo = "Ka", Ano = 2020, Cliente = cliente, DataCriacao = DateTime.UtcNow };
            db.AddRange(cliente, veiculo);
            await db.SaveChangesAsync();

            var criada = await sp.GetRequiredService<OrdemServicoUseCases>()
                .CriarAsync(new CreateOrdemServicoDto(cliente.Id, veiculo.Id, "Barulho no motor", null));
            Assert.True(criada.Success, criada.Error);
            _osId = criada.Data!.Id;
            _numeroOS = criada.Data.NumeroOS;
            _correlationId = await db.OrdensServico.Where(o => o.Id == _osId).Select(o => o.CorrelationId).SingleAsync();
        });

        // Commit feito: o "outbox" entrega o OrdemServicoAberta e a saga nasce.
        await _ambiente.Publicador.EntregarAsync(_ambiente.Harness.Bus);
        await _ambiente.EsperarEstadoAsync(_correlationId, "AguardandoDiagnostico");
        // O comando chega ao execution simulado de forma assíncrona, depois da transição.
        await AmbienteSaga.EsperarAsync(
            () => Task.FromResult(_ambiente.Simulados.Recebidos<SolicitarDiagnostico>().Any(c => c.CorrelationId == _correlationId)),
            () => "o execution-service não recebeu SolicitarDiagnostico.");
    }

    [Given("que o execution-service vai rejeitar a execução")]
    public void DadoQueAExecucaoSeraRejeitada() => _ambiente.Simulados.RejeitarExecucao = true;

    // ───────────── Quando ─────────────

    [When("o diagnóstico é concluído com {int} serviço e {int} peça")]
    public async Task QuandoODiagnosticoEConcluido(int servicos, int pecas)
    {
        await _ambiente.PublicarAsync(Msg<DiagnosticoIniciado>() with { Mecanico = "Carlos" });
        await _ambiente.EsperarEstadoAsync(_correlationId, "EmDiagnostico");

        // O mecânico registra o que a OS precisa durante o diagnóstico (a peça sai do estoque).
        await _ambiente.NoEscopoAsync(async sp =>
        {
            var useCases = sp.GetRequiredService<OrdemServicoUseCases>();
            var servico = await useCases.AdicionarItemAsync(_osId, new AddItemDto(TipoItemOrdemServico.Servico, _servicoId, null, servicos));
            Assert.True(servico.Success, servico.Error);
            var peca = await useCases.AdicionarItemAsync(_osId, new AddItemDto(TipoItemOrdemServico.Peca, null, _pecas.Values.Single(), pecas));
            Assert.True(peca.Success, peca.Error);
        });

        await _ambiente.PublicarAsync(Msg<DiagnosticoConcluido>() with { Observacoes = "Trocar óleo e filtro" });
        // O billing simulado gera o orçamento e o envia ao cliente.
        await _ambiente.EsperarEstadoAsync(_correlationId, "AguardandoAprovacao");
    }

    [When("o cliente aprova o orçamento")]
    public async Task QuandoOClienteAprova()
    {
        await _ambiente.PublicarAsync(Msg<OrcamentoAprovado>() with
        {
            OrcamentoId = _ambiente.Simulados.OrcamentoId!.Value,
            PagamentoId = _pagamentoId,
            LinkPagamento = "https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=teste"
        });
        await _ambiente.EsperarEstadoAsync(_correlationId, "AguardandoPagamento");
    }

    [When("o cliente recusa o orçamento")]
    public async Task QuandoOClienteRecusa()
    {
        await _ambiente.PublicarAsync(Msg<OrcamentoRecusado>() with
        {
            OrcamentoId = _ambiente.Simulados.OrcamentoId!.Value,
            Motivo = "Valor acima do esperado"
        });
        await _ambiente.EsperarEstadoAsync(_correlationId, "Cancelada");
    }

    [When("o pagamento é confirmado pelo Mercado Pago")]
    public async Task QuandoOPagamentoEConfirmado()
    {
        await _ambiente.PublicarAsync(Msg<PagamentoConfirmado>() with { PagamentoId = _pagamentoId, MpPaymentId = "1234567890", Valor = 165.90m });
        // O execution simulado inicia a execução (ou rejeita, conforme o cenário).
        await _ambiente.EsperarEstadoAsync(_correlationId, "EmExecucao", "AguardandoEstorno", "Cancelada");
    }

    [When("o pagamento é recusado pelo Mercado Pago")]
    public async Task QuandoOPagamentoERecusado()
    {
        await _ambiente.PublicarAsync(Msg<PagamentoRecusado>() with { PagamentoId = _pagamentoId, Motivo = "cc_rejected_insufficient_amount" });
        await _ambiente.EsperarEstadoAsync(_correlationId, "Cancelada");
    }

    [When("a execução é finalizada")]
    public async Task QuandoAExecucaoEFinalizada()
    {
        await _ambiente.PublicarAsync(Msg<ExecucaoFinalizada>() with { DuracaoSegundos = 3600 });
        await _ambiente.EsperarEstadoAsync(_correlationId, "Concluida");
    }

    [When("o prazo de aprovação do orçamento expira")]
    public async Task QuandoOPrazoDeAprovacaoExpira()
    {
        // O que o VerificadorPrazosSaga publica quando o prazo vence.
        await _ambiente.PublicarAsync(Msg<PrazoExpirado>() with { EstadoNoMomento = "AguardandoAprovacao" });
        await _ambiente.EsperarEstadoAsync(_correlationId, "Cancelada");
    }

    // ───────────── Então ─────────────

    [Then("a OS deve estar {string}")]
    public Task EntaoAOsDeveEstar(string status) =>
        AmbienteSaga.EsperarAsync(
            async () => (await StatusDaOsAsync()) == status,
            () => $"a OS deveria estar {status}, mas está em {StatusDaOsAsync().GetAwaiter().GetResult()}.");

    [Then("o histórico deve conter as transições {string}")]
    public async Task EntaoOHistoricoDeveConter(string transicoes)
    {
        var esperado = transicoes.Split(',', StringSplitOptions.TrimEntries);
        var historico = await _ambiente.NoEscopoAsync(sp => sp.GetRequiredService<AppDbContext>().HistoricoStatus.AsNoTracking()
            .Where(h => h.OrdemServicoId == _osId)
            .OrderBy(h => h.Id)
            .Select(h => h.StatusNovo)
            .ToListAsync());

        Assert.Equal(esperado, historico.Select(s => s.ToString()));
    }

    [Then("a peça {string} deve ter {int} unidades em estoque")]
    public Task EntaoAPecaDeveTer(string nome, int estoque) =>
        AmbienteSaga.EsperarAsync(
            async () => (await EstoqueAsync(nome)) == estoque,
            () => $"a peça {nome} deveria ter {estoque} unidades, mas tem {EstoqueAsync(nome).GetAwaiter().GetResult()}.");

    [Then("o execution-service deve receber o cancelamento da execução")]
    public Task EntaoOExecutionRecebeOCancelamento() =>
        AmbienteSaga.EsperarAsync(
            () => Task.FromResult(_ambiente.Simulados.Recebidos<CancelarExecucao>().Any(c => c.CorrelationId == _correlationId)),
            () => "o execution-service não recebeu CancelarExecucao.");

    [Then("o billing-service deve receber o cancelamento do orçamento")]
    public Task EntaoOBillingRecebeOCancelamento() =>
        AmbienteSaga.EsperarAsync(
            () => Task.FromResult(_ambiente.Simulados.Recebidos<CancelarOrcamento>().Any(c => c.CorrelationId == _correlationId)),
            () => "o billing-service não recebeu CancelarOrcamento.");

    [Then("o billing-service deve receber o estorno do pagamento")]
    public Task EntaoOBillingRecebeOEstorno() =>
        AmbienteSaga.EsperarAsync(
            () => Task.FromResult(_ambiente.Simulados.Recebidos<EstornarPagamento>()
                .Any(c => c.CorrelationId == _correlationId && c.PagamentoId == _pagamentoId)),
            () => "o billing-service não recebeu EstornarPagamento com o pagamento da OS.");

    [Then("a saga deve terminar em {string}")]
    public Task EntaoASagaDeveTerminarEm(string estado) => _ambiente.EsperarEstadoAsync(_correlationId, estado);

    // ───────────── apoio ─────────────

    private T Msg<T>() where T : MensagemOS, new() => new()
    {
        CorrelationId = _correlationId,
        OsId = _osId,
        NumeroOS = _numeroOS,
        OcorridoEm = DateTime.UtcNow
    };

    private Task<string> StatusDaOsAsync() =>
        _ambiente.NoEscopoAsync(async sp => (await sp.GetRequiredService<AppDbContext>().OrdensServico.AsNoTracking()
            .SingleAsync(o => o.Id == _osId)).Status.ToString());

    private Task<int> EstoqueAsync(string nome) =>
        _ambiente.NoEscopoAsync(async sp => (await sp.GetRequiredService<AppDbContext>().Pecas.AsNoTracking()
            .SingleAsync(p => p.Id == _pecas[nome])).QuantidadeEstoque);
}
