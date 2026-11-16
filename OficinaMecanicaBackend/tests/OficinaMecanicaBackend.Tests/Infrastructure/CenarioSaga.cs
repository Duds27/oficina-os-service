using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oficina.Contracts;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Infrastructure.Data;

namespace OficinaMecanicaBackend.Tests.Infrastructure;

public record OsCriada(int Id, string NumeroOS, Guid CorrelationId, int PecaId);

/// <summary>
/// Roteiro da saga para os testes de integração: cria a OS pela API e simula o billing e o
/// execution publicando os eventos no bus do test harness.
/// </summary>
public static class CenarioSaga
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(20);

    private record IdResult(int Id);
    private record OsResult(int Id, string NumeroOS);

    /// <summary>Cria cliente, veículo e OS (com 1 serviço e 2 unidades de 1 peça, se pedido).</summary>
    public static async Task<OsCriada> CriarOsAsync(HttpClient client, IServiceProvider services, bool comPeca = false)
    {
        var token = await CustomWebApplicationFactory.GetAuthTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var cliente = await (await client.PostAsJsonAsync("/api/clientes",
            new { CpfCnpj = GerarCpf(), Nome = "Cliente Saga", Email = "saga@example.com" })).Content.ReadFromJsonAsync<IdResult>();
        var veiculo = await (await client.PostAsJsonAsync("/api/veiculos",
            new { Placa = $"SAG{Random.Shared.Next(1000, 9999)}", Marca = "Fiat", Modelo = "Uno", Ano = 2015, ClienteId = cliente!.Id })).Content.ReadFromJsonAsync<IdResult>();

        var resp = await client.PostAsJsonAsync("/api/ordens-servico", new { ClienteId = cliente.Id, VeiculoId = veiculo!.Id });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var os = (await resp.Content.ReadFromJsonAsync<OsResult>())!;

        var pecaId = 0;
        if (comPeca)
        {
            var servico = await (await client.PostAsJsonAsync("/api/servicos",
                new { Nome = "Revisão", PrecoBase = 50m, Ativo = true })).Content.ReadFromJsonAsync<IdResult>();
            var peca = await (await client.PostAsJsonAsync("/api/pecas",
                new { Nome = "Vela", PrecoUnitario = 30m, QuantidadeEstoque = 10, QuantidadeMinima = 1, Ativo = true })).Content.ReadFromJsonAsync<IdResult>();
            pecaId = peca!.Id;
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/ordens-servico/{os.Id}/itens",
                new { Tipo = (int)TipoItemOrdemServico.Servico, ServicoId = servico!.Id, Quantidade = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/ordens-servico/{os.Id}/itens",
                new { Tipo = (int)TipoItemOrdemServico.Peca, PecaId = pecaId, Quantidade = 2 })).StatusCode);
        }

        await using var escopo = services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var correlationId = await db.OrdensServico.Where(o => o.Id == os.Id).Select(o => o.CorrelationId).SingleAsync();
        return new OsCriada(os.Id, os.NumeroOS, correlationId, pecaId);
    }

    public static T Msg<T>(OsCriada os) where T : MensagemOS, new() => new()
    {
        CorrelationId = os.CorrelationId,
        OsId = os.Id,
        NumeroOS = os.NumeroOS,
        OcorridoEm = DateTime.UtcNow
    };

    /// <summary>Espera a saga chegar ao estado, lendo do MySQL.</summary>
    public static async Task<OrdemServicoSagaState> EsperarSagaAsync(IServiceProvider services, Guid correlationId, string estado)
    {
        var limite = DateTime.UtcNow + Espera;
        OrdemServicoSagaState? saga = null;
        while (DateTime.UtcNow < limite)
        {
            await using var escopo = services.CreateAsyncScope();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            saga = await db.Set<OrdemServicoSagaState>().AsNoTracking().SingleOrDefaultAsync(s => s.CorrelationId == correlationId);
            if (saga?.CurrentState == estado)
                return saga;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"A saga não chegou a {estado}; está em {saga?.CurrentState ?? "(não existe)"}.");
    }

    /// <summary>
    /// Leva a saga do início até um estado do caminho feliz, um evento por vez, esperando
    /// cada transição. Devolve o PagamentoId usado.
    /// </summary>
    public static async Task<Guid> LevarAteAsync(ITestHarness harness, IServiceProvider services, OsCriada os, string estadoFinal)
    {
        var orcamentoId = Guid.NewGuid();
        var pagamentoId = Guid.NewGuid();
        var passos = new (string Estado, object Evento)[]
        {
            ("EmDiagnostico", Msg<DiagnosticoIniciado>(os) with { Mecanico = "Carlos" }),
            ("AguardandoOrcamento", Msg<DiagnosticoConcluido>(os)),
            ("AguardandoAprovacao", Msg<OrcamentoGerado>(os) with { OrcamentoId = orcamentoId, ValorTotal = 110m, ValidoAte = DateTime.UtcNow.AddDays(2) }),
            ("AguardandoPagamento", Msg<OrcamentoAprovado>(os) with { OrcamentoId = orcamentoId, PagamentoId = pagamentoId, LinkPagamento = "https://mp.example/1" }),
            ("AguardandoExecucao", Msg<PagamentoConfirmado>(os) with { PagamentoId = pagamentoId, MpPaymentId = "999", Valor = 110m }),
            ("EmExecucao", Msg<ExecucaoIniciada>(os)),
            ("Concluida", Msg<ExecucaoFinalizada>(os) with { DuracaoSegundos = 60 })
        };

        await EsperarSagaAsync(services, os.CorrelationId, "AguardandoDiagnostico");
        foreach (var (estado, evento) in passos)
        {
            await harness.Bus.Publish(evento, evento.GetType());
            await EsperarSagaAsync(services, os.CorrelationId, estado);
            if (estado == estadoFinal)
                return pagamentoId;
        }
        throw new ArgumentException($"Estado fora do caminho feliz: {estadoFinal}", nameof(estadoFinal));
    }

    /// <summary>CPF válido aleatório (o cadastro de cliente valida os dígitos).</summary>
    public static string GerarCpf()
    {
        var d = Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10)).ToList();
        for (var t = 9; t < 11; t++)
        {
            var soma = Enumerable.Range(0, t).Sum(i => d[i] * (t + 1 - i));
            d.Add(soma * 10 % 11 % 10);
        }
        return string.Concat(d);
    }
}
