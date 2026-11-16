using MassTransit;
using MassTransit.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oficina.Contracts;
using OficinaMecanica.Application;
using OficinaMecanica.Application.Abstractions;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Infrastructure;
using OficinaMecanica.Infrastructure.Data;

namespace OficinaMecanica.BddTests.Suporte;

/// <summary>
/// O os-service com a saga real, os casos de uso e repositórios reais sobre SQLite, e o
/// test harness do MassTransit (transporte e repositório da saga em memória). Um por cenário.
///
/// O SQLite fica num arquivo temporário, não em memória: cada DbContext abre a própria
/// conexão, porque o bus processa mensagens em outras threads enquanto o cenário lê o banco.
/// </summary>
public sealed class AmbienteSaga : IAsyncDisposable
{
    private static readonly TimeSpan EsperaPadrao = TimeSpan.FromSeconds(10);

    private readonly string _arquivo;
    private readonly ServiceProvider _provider;

    private AmbienteSaga(string arquivo, ServiceProvider provider)
    {
        _arquivo = arquivo;
        _provider = provider;
        Harness = provider.GetRequiredService<ITestHarness>();
        Saga = Harness.GetSagaStateMachineHarness<OrdemServicoStateMachine, OrdemServicoSagaState>();
        Simulados = provider.GetRequiredService<ServicosSimulados>();
        Publicador = provider.GetRequiredService<PublicadorAposCommit>();
    }

    public ITestHarness Harness { get; }
    public ISagaStateMachineTestHarness<OrdemServicoStateMachine, OrdemServicoSagaState> Saga { get; }
    public ServicosSimulados Simulados { get; }
    public PublicadorAposCommit Publicador { get; }

    public static async Task<AmbienteSaga> CriarAsync()
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"oficina-bdd-{Guid.NewGuid():N}.db");
        var publicador = new PublicadorAposCommit();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={arquivo}"));
        services.AddPersistencia();
        services.AddApplication();
        services.AddSingleton(publicador);
        services.AddSingleton<IPublicadorEventos>(publicador);
        services.AddSingleton<ServicosSimulados>();
        services.Configure<PrazosSagaOptions>(_ => { });

        services.AddMassTransitTestHarness(x =>
        {
            x.AddSagaStateMachine<OrdemServicoStateMachine, OrdemServicoSagaState>()
                .InMemoryRepository()
                .Endpoint(e => e.Name = FilasOs.Saga);

            x.AddConsumer<SolicitarDiagnosticoSimulado>().Endpoint(e => e.Name = "execution-solicitar-diagnostico");
            x.AddConsumer<IniciarExecucaoSimulado>().Endpoint(e => e.Name = "execution-iniciar-execucao");
            x.AddConsumer<CancelarExecucaoSimulado>().Endpoint(e => e.Name = "execution-cancelar-execucao");
            x.AddConsumer<GerarOrcamentoSimulado>().Endpoint(e => e.Name = "billing-gerar-orcamento");
            x.AddConsumer<CancelarOrcamentoSimulado>().Endpoint(e => e.Name = "billing-cancelar-orcamento");
            x.AddConsumer<EstornarPagamentoSimulado>().Endpoint(e => e.Name = "billing-estornar-pagamento");

            x.AddConfigureEndpointsCallback((context, _, endpoint) =>
                endpoint.UseConsumeFilter(typeof(SalvarDbContextFilter<>), context));

            x.SetTestTimeouts(testInactivityTimeout: TimeSpan.FromMilliseconds(200));
        });

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using (var escopo = provider.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        }

        var ambiente = new AmbienteSaga(arquivo, provider);
        await ambiente.Harness.Start();
        return ambiente;
    }

    /// <summary>Executa a ação num escopo novo (um DbContext novo, como uma requisição).</summary>
    public async Task<T> NoEscopoAsync<T>(Func<IServiceProvider, Task<T>> acao)
    {
        await using var escopo = _provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider);
    }

    public Task NoEscopoAsync(Func<IServiceProvider, Task> acao) =>
        NoEscopoAsync<object?>(async sp => { await acao(sp); return null; });

    public Task PublicarAsync<T>(T mensagem) where T : class => Harness.Bus.Publish(mensagem);

    public string? EstadoDaSaga(Guid correlationId) => Saga.Sagas.Contains(correlationId)?.CurrentState;

    /// <summary>Espera a condição ficar verdadeira, com prazo.</summary>
    public static async Task EsperarAsync(Func<Task<bool>> condicao, Func<string> descricao, TimeSpan? espera = null)
    {
        var limite = DateTime.UtcNow + (espera ?? EsperaPadrao);
        while (!await condicao())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"Tempo esgotado: {descricao()}");
            await Task.Delay(25);
        }
    }

    public Task EsperarEstadoAsync(Guid correlationId, params string[] estados) =>
        EsperarAsync(
            () => Task.FromResult(estados.Contains(EstadoDaSaga(correlationId))),
            () => $"a saga deveria chegar a {string.Join(" ou ", estados)}, mas está em {EstadoDaSaga(correlationId) ?? "(não existe)"}.");

    public async ValueTask DisposeAsync()
    {
        await Harness.Stop();
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_arquivo); } catch (IOException) { /* o SO limpa a pasta temporária */ }
    }
}
