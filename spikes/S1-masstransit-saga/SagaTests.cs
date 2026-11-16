using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Infrastructure.Data;
using Spike.Os;
using Spike.Os.Contracts;
using Xunit.Abstractions;
using PingSemEntityName = Spike.Os.Contracts.SemEntityName.Ping;

namespace Spike.S1;

[Collection(nameof(SpikeCollection))]
public class SagaTests(SpikeFixture f, ITestOutputHelper output)
{
    private static readonly TimeSpan Limite = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pergunta principal do S1: OS + evento gravados na mesma transação (outbox) →
    /// saga criada no MySQL → comando enviado ao "execution" (outro namespace) →
    /// evento de volta → saga e status da OS atualizados juntos.
    /// </summary>
    [Fact]
    public async Task Abertura_da_OS_cria_a_saga_no_MySQL_e_avanca_com_o_evento_do_outro_servico()
    {
        var correlationId = NewId.NextGuid();
        var placa = $"SPK{Random.Shared.Next(1000, 9999)}";
        int osId;

        await using (var scope = f.Os.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            await using var tx = await db.Database.BeginTransactionAsync();

            var cliente = new Cliente
            {
                CpfCnpj = Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString(),
                Nome = "Cliente Spike",
                DataCriacao = DateTime.UtcNow
            };
            var veiculo = new Veiculo { Placa = placa, Marca = "Fiat", Modelo = "Uno", Ano = 2015, Cliente = cliente, DataCriacao = DateTime.UtcNow };
            var os = new OrdemServico
            {
                NumeroOS = $"OS-SPIKE-{Random.Shared.Next(100000, 999999)}",
                Cliente = cliente,
                Veiculo = veiculo,
                Descricao = "Barulho no motor",
                DataCriacao = DateTime.UtcNow
            };
            db.AddRange(cliente, veiculo, os);
            await db.SaveChangesAsync(); // gera o Id da OS dentro da transação

            await publish.Publish(new OrdemServicoAberta
            {
                CorrelationId = correlationId,
                OsId = os.Id,
                NumeroOS = os.NumeroOS,
                OcorridoEm = DateTime.UtcNow
            });
            await db.SaveChangesAsync(); // grava a mensagem no outbox
            await tx.CommitAsync();
            osId = os.Id;
        }

        var saga = await EsperarSaga(correlationId, s => s?.CurrentState == "EmDiagnostico");
        DespejarLogsSe(saga?.CurrentState != "EmDiagnostico");

        Assert.NotNull(saga);
        Assert.Equal("EmDiagnostico", saga.CurrentState);
        Assert.Equal(osId, saga.OsId);
        Assert.NotEqual(default, saga.RowVersion);

        // O comando chegou ao "execution" e foi desserializado no tipo do outro namespace.
        Assert.True(f.Recebidos.Diagnosticos.TryGetValue(correlationId, out var comando));
        Assert.Equal(placa, comando.Veiculo.Placa);
        Assert.Equal("Barulho no motor", comando.Descricao);

        // O evento publicado pelo outbox também chegou a um assinante de outro namespace.
        Assert.True(f.Recebidos.OrdensAbertas.ContainsKey(correlationId));

        // A activity alterou a OS no mesmo DbContext/transação da saga.
        await using var verificacao = f.Os.Services.CreateAsyncScope();
        var status = await verificacao.ServiceProvider.GetRequiredService<AppDbContext>()
            .OrdensServico.AsNoTracking().Where(o => o.Id == osId).Select(o => o.Status).SingleAsync();
        Assert.Equal(StatusOrdemServico.EmDiagnostico, status);
    }

    /// <summary>Sem SaveChanges, o outbox não entrega nada: a mensagem só sai se a transação for gravada.</summary>
    [Fact]
    public async Task Sem_SaveChanges_nenhuma_mensagem_sai_do_outbox()
    {
        var correlationId = NewId.NextGuid();

        await using (var scope = f.Os.Services.CreateAsyncScope())
        {
            var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            await publish.Publish(new OrdemServicoAberta
            {
                CorrelationId = correlationId,
                OsId = int.MaxValue,
                NumeroOS = "OS-NAO-SALVA",
                OcorridoEm = DateTime.UtcNow
            });
            // sem SaveChanges: simula exceção no caso de uso antes do commit
        }

        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.False(f.Recebidos.OrdensAbertas.ContainsKey(correlationId));
        Assert.Null(await ObterSaga(correlationId));
    }

    /// <summary>O RowVersion (timestamp(6) do MySQL) detecta duas atualizações simultâneas da mesma saga.</summary>
    [Fact]
    public async Task RowVersion_no_MySQL_detecta_atualizacao_concorrente_da_saga()
    {
        var correlationId = NewId.NextGuid();
        await using (var scope = f.Os.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Set<OrdemServicoSagaState>().Add(new OrdemServicoSagaState
            {
                CorrelationId = correlationId,
                CurrentState = "AguardandoDiagnostico",
                OsId = Random.Shared.Next(10_000_000, int.MaxValue),
                NumeroOS = "OS-CONCORRENCIA",
                CriadoEm = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var s1 = f.Os.Services.CreateAsyncScope();
        await using var s2 = f.Os.Services.CreateAsyncScope();
        var db1 = s1.ServiceProvider.GetRequiredService<AppDbContext>();
        var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();

        var a = await db1.Set<OrdemServicoSagaState>().SingleAsync(x => x.CorrelationId == correlationId);
        var b = await db2.Set<OrdemServicoSagaState>().SingleAsync(x => x.CorrelationId == correlationId);

        a.CurrentState = "EmDiagnostico";
        await db1.SaveChangesAsync();

        b.CurrentState = "Cancelada";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    /// <summary>
    /// Sem [EntityName], produtor e consumidor em namespaces diferentes usam exchanges diferentes
    /// e a mensagem não chega (sem erro). Confirma que o EntityName é necessário.
    /// </summary>
    [Fact]
    public async Task Sem_EntityName_mensagem_entre_namespaces_diferentes_nao_chega()
    {
        var id = NewId.NextGuid();
        await f.Os.Services.GetRequiredService<IBus>().Publish(new PingSemEntityName { Id = id });

        var entregue = await Ate(() => Task.FromResult(f.Recebidos.Pings.ContainsKey(id)), TimeSpan.FromSeconds(10));
        var exchanges = await f.ListarExchangesAsync();
        output.WriteLine($"Ping sem EntityName entregue: {entregue}");
        output.WriteLine("Exchanges com 'Ping': " + string.Join(", ", exchanges.Where(e => e.Contains("Ping"))));

        Assert.False(entregue);
    }

    /// <summary>Os exchanges levam o nome do EntityName e os tipos base do envelope não viram exchange.</summary>
    [Fact]
    public async Task Exchanges_usam_o_EntityName_e_o_envelope_nao_vira_exchange()
    {
        // Garante que os tipos já foram publicados pelo menos uma vez.
        await Abertura_da_OS_cria_a_saga_no_MySQL_e_avanca_com_o_evento_do_outro_servico();

        var exchanges = await f.ListarExchangesAsync();
        output.WriteLine(string.Join(Environment.NewLine, exchanges));

        Assert.Contains("oficina.ordem-servico-aberta.v1", exchanges);
        Assert.Contains("oficina.diagnostico-iniciado.v1", exchanges);
        Assert.DoesNotContain(exchanges, e => e.Contains("MensagemOS"));
        Assert.DoesNotContain(exchanges, e => e.Contains("Spike.Os.Contracts:") || e.Contains("Spike.Execution.Contracts:"));
    }

    /// <summary>
    /// RabbitMQ 4.x depreciou filas transitórias não exclusivas (transient_nonexcl_queues).
    /// Confere que o MassTransit 8.4.1 não usa esse recurso. O único aviso esperado é o
    /// management_metrics_collection, do próprio plugin de management (independe do MassTransit).
    /// </summary>
    [Fact]
    public async Task MassTransit_nao_usa_recurso_depreciado_do_RabbitMq_4_1()
    {
        var logs = await f.LogsDoRabbitAsync();
        var avisos = logs.Split('\n').Where(l => l.Contains("deprecated", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var a in avisos) output.WriteLine(a);

        Assert.DoesNotContain(avisos, a => a.Contains("transient_nonexcl_queues"));
        Assert.All(avisos, a => Assert.Contains("management_metrics_collection", a));
    }

    private async Task<OrdemServicoSagaState?> ObterSaga(Guid correlationId)
    {
        await using var scope = f.Os.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<OrdemServicoSagaState>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CorrelationId == correlationId);
    }

    private async Task<OrdemServicoSagaState?> EsperarSaga(Guid correlationId, Func<OrdemServicoSagaState?, bool> condicao)
    {
        OrdemServicoSagaState? saga = null;
        await Ate(async () => condicao(saga = await ObterSaga(correlationId)), Limite);
        return saga;
    }

    private static async Task<bool> Ate(Func<Task<bool>> condicao, TimeSpan limite)
    {
        var fim = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < fim)
        {
            if (await condicao()) return true;
            await Task.Delay(200);
        }
        return await condicao();
    }

    private void DespejarLogsSe(bool falhou)
    {
        if (!falhou) return;
        foreach (var l in f.Logs) output.WriteLine(l);
    }
}
