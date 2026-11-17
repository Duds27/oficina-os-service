using System.Net.Http.Json;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oficina.Contracts;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Infrastructure.Data;
using OficinaMecanica.Infrastructure.Messaging;
using OficinaMecanicaBackend.Tests.Infrastructure;
using static OficinaMecanicaBackend.Tests.Infrastructure.CenarioSaga;

namespace OficinaMecanicaBackend.Tests.Integration;

/// <summary>
/// Saga com o repositório EF no MySQL real, outbox e inbox (transporte em memória do
/// harness no lugar do RabbitMQ). Os outros serviços são simulados publicando os eventos.
/// </summary>
[Collection("Integration")]
public class SagaIntegracaoTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestHarness _harness;

    public SagaIntegracaoTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _harness = factory.Services.GetTestHarness();
    }

    private record PecaResult(int Id, int QuantidadeEstoque);

    [Fact]
    public async Task AberturaDaOS_PersisteASagaNoMySqlEEnviaSolicitarDiagnostico()
    {
        var os = await CriarOsAsync(_client, _factory.Services);

        var saga = await EsperarSagaAsync(_factory.Services, os.CorrelationId, "AguardandoDiagnostico");
        Assert.Equal(os.Id, saga.OsId);
        Assert.Equal(os.NumeroOS, saga.NumeroOS);
        Assert.NotEqual(default, saga.RowVersion);

        var comando = await _harness.EsperarConsumidaAsync<SolicitarDiagnostico>(m => m.CorrelationId == os.CorrelationId);
        Assert.Equal(os.Id, comando.OsId);
    }

    [Fact]
    public async Task CaminhoFeliz_StatusEHistoricoDaOsSaoGravadosPelaSaga()
    {
        var os = await CriarOsAsync(_client, _factory.Services, comPeca: true);

        var pagamentoId = await LevarAteAsync(_harness, _factory.Services, os, "Concluida");

        var gerar = await _harness.EsperarConsumidaAsync<GerarOrcamento>(m => m.CorrelationId == os.CorrelationId);
        Assert.Equal(2, gerar.Itens.Count);
        Assert.Equal(110m, gerar.ValorTotal);
        var iniciar = await _harness.EsperarConsumidaAsync<IniciarExecucao>(m => m.CorrelationId == os.CorrelationId);
        Assert.Equal("Revisão", Assert.Single(iniciar.Servicos).Descricao);

        var saga = await EsperarSagaAsync(_factory.Services, os.CorrelationId, "Concluida");
        Assert.Equal(pagamentoId, saga.PagamentoId);

        await using var escopo = _factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var ordem = await db.OrdensServico.AsNoTracking().SingleAsync(o => o.Id == os.Id);
        Assert.Equal(StatusOrdemServico.Finalizada, ordem.Status);
        Assert.True(ordem.OrcamentoAprovado);
        Assert.NotNull(ordem.DataInicioExecucao);
        Assert.NotNull(ordem.DataFinalizacao);

        var historico = await db.HistoricoStatus.AsNoTracking()
            .Where(h => h.OrdemServicoId == os.Id).OrderBy(h => h.Id).ToListAsync();
        Assert.Equal(
            [StatusOrdemServico.EmDiagnostico, StatusOrdemServico.AguardandoAprovacao, StatusOrdemServico.AguardandoPagamento,
             StatusOrdemServico.EmExecucao, StatusOrdemServico.Finalizada],
            historico.Select(h => h.StatusNovo));
        Assert.All(historico, h =>
        {
            Assert.Equal(OrigemMudancaStatus.Saga, h.Origem);
            Assert.Equal(os.CorrelationId, h.CorrelationId);
        });
        // Uma notificação (e-mail simulado) por transição.
        Assert.Equal(5, await db.NotificacoesOutbox.CountAsync(n => n.OrdemServicoId == os.Id));
    }

    [Fact]
    public async Task OrcamentoRecusado_DevolveAPecaAoEstoqueNaMesmaTransacaoDoCancelamento()
    {
        var os = await CriarOsAsync(_client, _factory.Services, comPeca: true);
        var estoqueComReserva = await EstoqueAsync(os.PecaId);
        await LevarAteAsync(_harness, _factory.Services, os, "AguardandoAprovacao");

        await _harness.Bus.Publish(Msg<OrcamentoRecusado>(os) with { Motivo = "Caro demais" });
        var saga = await EsperarSagaAsync(_factory.Services, os.CorrelationId, "Cancelada");

        Assert.Contains("Caro demais", saga.MotivoCancelamento);
        Assert.Equal(estoqueComReserva + 2, await EstoqueAsync(os.PecaId));
        await using var escopo = _factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var ordem = await db.OrdensServico.AsNoTracking().SingleAsync(o => o.Id == os.Id);
        Assert.Equal(StatusOrdemServico.Cancelada, ordem.Status);
        Assert.NotNull(ordem.PecasDevolvidasEm);
        await _harness.EsperarConsumidaAsync<CancelarExecucao>(m => m.CorrelationId == os.CorrelationId);
        Assert.Empty(_harness.Consumidas<CancelarOrcamento>(m => m.CorrelationId == os.CorrelationId));
    }

    [Fact]
    public async Task ExecucaoRejeitada_EstornaEFechaQuandoOBillingConfirma()
    {
        var os = await CriarOsAsync(_client, _factory.Services, comPeca: true);
        var estoqueComReserva = await EstoqueAsync(os.PecaId);
        var pagamentoId = await LevarAteAsync(_harness, _factory.Services, os, "AguardandoExecucao");

        await _harness.Bus.Publish(Msg<ExecucaoRejeitada>(os) with { Motivo = "Sem mecânico" });
        await EsperarSagaAsync(_factory.Services, os.CorrelationId, "AguardandoEstorno");
        var estorno = await _harness.EsperarConsumidaAsync<EstornarPagamento>(m => m.CorrelationId == os.CorrelationId);
        Assert.Equal(pagamentoId, estorno.PagamentoId);
        Assert.Equal(estoqueComReserva + 2, await EstoqueAsync(os.PecaId));

        await _harness.Bus.Publish(Msg<PagamentoEstornado>(os) with { PagamentoId = pagamentoId, Valor = 110m });
        await EsperarSagaAsync(_factory.Services, os.CorrelationId, "Cancelada");
    }

    [Fact]
    public async Task RowVersion_NoMySql_DetectaAtualizacaoConcorrenteDaSaga()
    {
        var os = await CriarOsAsync(_client, _factory.Services);
        await EsperarSagaAsync(_factory.Services, os.CorrelationId, "AguardandoDiagnostico");

        await using var escopo1 = _factory.Services.CreateAsyncScope();
        await using var escopo2 = _factory.Services.CreateAsyncScope();
        var db1 = escopo1.ServiceProvider.GetRequiredService<AppDbContext>();
        var db2 = escopo2.ServiceProvider.GetRequiredService<AppDbContext>();
        var saga1 = await db1.Set<OrdemServicoSagaState>().SingleAsync(s => s.CorrelationId == os.CorrelationId);
        var saga2 = await db2.Set<OrdemServicoSagaState>().SingleAsync(s => s.CorrelationId == os.CorrelationId);

        saga1.AtualizadoEm = DateTime.UtcNow;
        await db1.SaveChangesAsync();

        saga2.AtualizadoEm = DateTime.UtcNow.AddSeconds(1);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task VerificadorDePrazos_PublicaPrazoExpiradoELimpaOPrazo()
    {
        var os = await CriarOsAsync(_client, _factory.Services);
        await EsperarSagaAsync(_factory.Services, os.CorrelationId, "AguardandoDiagnostico");
        // Simula uma saga parada em AguardandoAprovacao com o prazo vencido há mais de duas
        // rodadas do verificador (30 s cada): conta em saga.prazos.atrasados.
        await using (var escopo = _factory.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            var saga = await db.Set<OrdemServicoSagaState>().SingleAsync(s => s.CorrelationId == os.CorrelationId);
            saga.CurrentState = "AguardandoAprovacao";
            saga.PrazoExpiraEm = DateTime.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync();
        }

        var avisos = await _factory.Services.GetRequiredService<VerificadorPrazosSaga>().VerificarAsync();

        Assert.True(avisos >= 1);
        // A contagem do gauge é feita antes de tratar os prazos da rodada.
        Assert.True(MetricasSaga.PrazosAtrasados >= 1);
        await _harness.EsperarPublicadaAsync<PrazoExpirado>(m =>
            m.CorrelationId == os.CorrelationId && m.EstadoNoMomento == "AguardandoAprovacao");
        // A saga recebe o aviso e aplica a C2.
        var cancelada = await EsperarSagaAsync(_factory.Services, os.CorrelationId, "Cancelada");
        Assert.Null(cancelada.PrazoExpiraEm);
        await _harness.EsperarConsumidaAsync<CancelarOrcamento>(m => m.CorrelationId == os.CorrelationId);
    }

    private async Task<int> EstoqueAsync(int pecaId) =>
        (await _client.GetFromJsonAsync<PecaResult>($"/api/pecas/{pecaId}"))!.QuantidadeEstoque;
}
