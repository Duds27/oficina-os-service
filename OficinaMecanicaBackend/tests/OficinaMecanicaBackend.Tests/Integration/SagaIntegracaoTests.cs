using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oficina.Contracts;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Infrastructure.Data;
using OficinaMecanica.Infrastructure.Messaging;
using OficinaMecanicaBackend.Tests.Infrastructure;

namespace OficinaMecanicaBackend.Tests.Integration;

/// <summary>
/// Saga com o repositório EF no MySQL real, outbox e inbox (transporte em memória do
/// harness no lugar do RabbitMQ). Os outros serviços são simulados publicando os eventos.
/// </summary>
[Collection("Integration")]
public class SagaIntegracaoTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(20);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestHarness _harness;

    public SagaIntegracaoTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _harness = factory.Services.GetTestHarness();
    }

    [Fact]
    public async Task AberturaDaOS_PersisteASagaNoMySqlEEnviaSolicitarDiagnostico()
    {
        var os = await CriarOsAsync();

        var saga = await EsperarSagaAsync(os.CorrelationId, "AguardandoDiagnostico");
        Assert.Equal(os.Id, saga.OsId);
        Assert.Equal(os.NumeroOS, saga.NumeroOS);
        Assert.NotEqual(default, saga.RowVersion);

        var comando = await _harness.EsperarConsumidaAsync<SolicitarDiagnostico>(m => m.CorrelationId == os.CorrelationId);
        Assert.Equal(os.Id, comando.OsId);
    }

    [Fact]
    public async Task CaminhoFeliz_StatusEHistoricoDaOsSaoGravadosPelaSaga()
    {
        var os = await CriarOsAsync(comPeca: true);
        await EsperarSagaAsync(os.CorrelationId, "AguardandoDiagnostico");

        await PublicarAsync(Msg<DiagnosticoIniciado>(os) with { Mecanico = "Carlos" });
        await EsperarSagaAsync(os.CorrelationId, "EmDiagnostico");
        await PublicarAsync(Msg<DiagnosticoConcluido>(os));
        await EsperarSagaAsync(os.CorrelationId, "AguardandoOrcamento");

        var gerar = await _harness.EsperarConsumidaAsync<GerarOrcamento>(m => m.CorrelationId == os.CorrelationId);
        Assert.Equal(2, gerar.Itens.Count);
        Assert.Equal(110m, gerar.ValorTotal);

        var orcamentoId = Guid.NewGuid();
        var pagamentoId = Guid.NewGuid();
        await PublicarAsync(Msg<OrcamentoGerado>(os) with { OrcamentoId = orcamentoId, ValorTotal = 110m, ValidoAte = DateTime.UtcNow.AddDays(2) });
        await EsperarSagaAsync(os.CorrelationId, "AguardandoAprovacao");
        await PublicarAsync(Msg<OrcamentoAprovado>(os) with { OrcamentoId = orcamentoId, PagamentoId = pagamentoId, LinkPagamento = "https://mp.example/1" });
        await EsperarSagaAsync(os.CorrelationId, "AguardandoPagamento");
        await PublicarAsync(Msg<PagamentoConfirmado>(os) with { PagamentoId = pagamentoId, MpPaymentId = "999", Valor = 110m });
        await EsperarSagaAsync(os.CorrelationId, "AguardandoExecucao");
        await PublicarAsync(Msg<ExecucaoIniciada>(os));
        await EsperarSagaAsync(os.CorrelationId, "EmExecucao");
        await PublicarAsync(Msg<ExecucaoFinalizada>(os) with { DuracaoSegundos = 60 });
        var saga = await EsperarSagaAsync(os.CorrelationId, "Concluida");

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
        var os = await CriarOsAsync(comPeca: true);
        var estoqueComReserva = await EstoqueAsync(os.PecaId);

        await EsperarSagaAsync(os.CorrelationId, "AguardandoDiagnostico");
        await PublicarAsync(Msg<DiagnosticoIniciado>(os));
        await EsperarSagaAsync(os.CorrelationId, "EmDiagnostico");
        await PublicarAsync(Msg<DiagnosticoConcluido>(os));
        await EsperarSagaAsync(os.CorrelationId, "AguardandoOrcamento");
        await PublicarAsync(Msg<OrcamentoGerado>(os) with { OrcamentoId = Guid.NewGuid() });
        await EsperarSagaAsync(os.CorrelationId, "AguardandoAprovacao");

        await PublicarAsync(Msg<OrcamentoRecusado>(os) with { Motivo = "Caro demais" });
        var saga = await EsperarSagaAsync(os.CorrelationId, "Cancelada");

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
    public async Task RowVersion_NoMySql_DetectaAtualizacaoConcorrenteDaSaga()
    {
        var os = await CriarOsAsync();
        await EsperarSagaAsync(os.CorrelationId, "AguardandoDiagnostico");

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
        var os = await CriarOsAsync();
        await EsperarSagaAsync(os.CorrelationId, "AguardandoDiagnostico");
        // Simula uma saga parada em AguardandoAprovacao com o prazo vencido.
        await using (var escopo = _factory.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            var saga = await db.Set<OrdemServicoSagaState>().SingleAsync(s => s.CorrelationId == os.CorrelationId);
            saga.CurrentState = "AguardandoAprovacao";
            saga.PrazoExpiraEm = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var avisos = await _factory.Services.GetRequiredService<VerificadorPrazosSaga>().VerificarAsync();

        Assert.True(avisos >= 1);
        await _harness.EsperarPublicadaAsync<PrazoExpirado>(m =>
            m.CorrelationId == os.CorrelationId && m.EstadoNoMomento == "AguardandoAprovacao");
        // A saga recebe o aviso e aplica a C2.
        var cancelada = await EsperarSagaAsync(os.CorrelationId, "Cancelada");
        Assert.Null(cancelada.PrazoExpiraEm);
        await _harness.EsperarConsumidaAsync<CancelarOrcamento>(m => m.CorrelationId == os.CorrelationId);
    }

    // ───────────── apoio ─────────────

    private record OsCriada(int Id, string NumeroOS, Guid CorrelationId, int PecaId);
    private record IdResult(int Id);
    private record OsResult(int Id, string NumeroOS);
    private record PecaResult(int Id, int QuantidadeEstoque);

    private async Task<OsCriada> CriarOsAsync(bool comPeca = false)
    {
        var token = await CustomWebApplicationFactory.GetAuthTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var cpf = GerarCpf();
        var cliente = await (await _client.PostAsJsonAsync("/api/clientes",
            new { CpfCnpj = cpf, Nome = "Cliente Saga", Email = "saga@example.com" })).Content.ReadFromJsonAsync<IdResult>();
        var veiculo = await (await _client.PostAsJsonAsync("/api/veiculos",
            new { Placa = $"SAG{Random.Shared.Next(1000, 9999)}", Marca = "Fiat", Modelo = "Uno", Ano = 2015, ClienteId = cliente!.Id })).Content.ReadFromJsonAsync<IdResult>();

        var resp = await _client.PostAsJsonAsync("/api/ordens-servico", new { ClienteId = cliente.Id, VeiculoId = veiculo!.Id });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var os = (await resp.Content.ReadFromJsonAsync<OsResult>())!;

        var pecaId = 0;
        if (comPeca)
        {
            var servico = await (await _client.PostAsJsonAsync("/api/servicos",
                new { Nome = "Revisão", PrecoBase = 50m, Ativo = true })).Content.ReadFromJsonAsync<IdResult>();
            var peca = await (await _client.PostAsJsonAsync("/api/pecas",
                new { Nome = "Vela", PrecoUnitario = 30m, QuantidadeEstoque = 10, QuantidadeMinima = 1, Ativo = true })).Content.ReadFromJsonAsync<IdResult>();
            pecaId = peca!.Id;
            Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync($"/api/ordens-servico/{os.Id}/itens",
                new { Tipo = (int)TipoItemOrdemServico.Servico, ServicoId = servico!.Id, Quantidade = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync($"/api/ordens-servico/{os.Id}/itens",
                new { Tipo = (int)TipoItemOrdemServico.Peca, PecaId = pecaId, Quantidade = 2 })).StatusCode);
        }

        await using var escopo = _factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var correlationId = await db.OrdensServico.Where(o => o.Id == os.Id).Select(o => o.CorrelationId).SingleAsync();
        return new OsCriada(os.Id, os.NumeroOS, correlationId, pecaId);
    }

    private async Task<int> EstoqueAsync(int pecaId) =>
        (await _client.GetFromJsonAsync<PecaResult>($"/api/pecas/{pecaId}"))!.QuantidadeEstoque;

    private static T Msg<T>(OsCriada os) where T : MensagemOS, new() => new()
    {
        CorrelationId = os.CorrelationId,
        OsId = os.Id,
        NumeroOS = os.NumeroOS,
        OcorridoEm = DateTime.UtcNow
    };

    private Task PublicarAsync<T>(T mensagem) where T : class => _harness.Bus.Publish(mensagem);

    private async Task<OrdemServicoSagaState> EsperarSagaAsync(Guid correlationId, string estado)
    {
        var limite = DateTime.UtcNow + Espera;
        OrdemServicoSagaState? saga = null;
        while (DateTime.UtcNow < limite)
        {
            await using var escopo = _factory.Services.CreateAsyncScope();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            saga = await db.Set<OrdemServicoSagaState>().AsNoTracking().SingleOrDefaultAsync(s => s.CorrelationId == correlationId);
            if (saga?.CurrentState == estado)
                return saga;
            await Task.Delay(100);
        }
        Assert.Fail($"A saga não chegou a {estado}; está em {saga?.CurrentState ?? "(não existe)"}.");
        return null!;
    }

    /// <summary>CPF válido aleatório (o cadastro de cliente valida os dígitos).</summary>
    private static string GerarCpf()
    {
        var d = Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10)).ToList();
        for (var t = 9; t < 11; t++)
        {
            var soma = Enumerable.Range(0, t).Sum(i => d[i] * (t + 1 - i));
            var dv = soma * 10 % 11 % 10;
            d.Add(dv);
        }
        return string.Concat(d);
    }
}
