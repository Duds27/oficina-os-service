using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Infrastructure.Data;
using Spike.Execution;
using Spike.Os;
using Testcontainers.MySql;
using Testcontainers.RabbitMq;

namespace Spike.S1;

[CollectionDefinition(nameof(SpikeCollection))]
public class SpikeCollection : ICollectionFixture<SpikeFixture>;

/// <summary>
/// Sobe MySQL 8.4 e RabbitMQ 4.1 (mesmas imagens dos composes) e dois hosts:
/// "os" (AppDbContext real + saga + outbox) e "execution" (consumidores, outro namespace).
/// </summary>
public sealed class SpikeFixture : IAsyncLifetime
{
    public const string VHost = "oficina";
    private const string RabbitUser = "oficina";
    private const string RabbitPass = "oficina_dev";

    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8.4")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:4.1-management")
        .WithUsername(RabbitUser)
        .WithPassword(RabbitPass)
        .WithEnvironment("RABBITMQ_DEFAULT_VHOST", VHost)
        .WithPortBinding(15672, true)
        .Build();

    public IHost Os { get; private set; } = null!;
    public IHost Execution { get; private set; } = null!;
    public Recebidos Recebidos { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mysql.StartAsync(), _rabbit.StartAsync());

        Os = CriarHostOs();
        Execution = CriarHostExecution();

        // As tabelas precisam existir antes do serviço de entrega do outbox começar a consultar.
        await using (var scope = Os.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        }

        await Execution.StartAsync();
        await Os.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Os.StopAsync();
        await Execution.StopAsync();
        Os.Dispose();
        Execution.Dispose();
        await _rabbit.DisposeAsync();
        await _mysql.DisposeAsync();
    }

    private IHost CriarHostOs()
    {
        var builder = Host.CreateApplicationBuilder();
        ConfigurarLogs(builder, "os");

        builder.Services.AddDbContext<AppDbContext>(o => o
            .UseMySql(_mysql.GetConnectionString(), new MySqlServerVersion(new Version(8, 4, 0)))
            .ReplaceService<IModelCustomizer, SagaModelCustomizer>());

        builder.Services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<AppDbContext>(o =>
            {
                o.UseMySql();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromMilliseconds(200);
            });

            x.AddSagaStateMachine<OrdemServicoStateMachine, OrdemServicoSagaState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ConcurrencyMode = ConcurrencyMode.Optimistic;
                    r.ExistingDbContext<AppDbContext>();
                    r.UseMySql();
                })
                .Endpoint(e => e.Name = "os-ordem-servico-saga");

            x.AddConfigureEndpointsCallback((context, _, cfg) =>
            {
                cfg.UseMessageRetry(r => r.Intervals(100, 250, 500, 1000));
                cfg.UseEntityFrameworkOutbox<AppDbContext>(context);
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                ConfigurarHost(cfg);
                cfg.ConfigureEndpoints(context);
            });
        });

        return builder.Build();
    }

    private IHost CriarHostExecution()
    {
        var builder = Host.CreateApplicationBuilder();
        ConfigurarLogs(builder, "execution");
        builder.Services.AddSingleton(Recebidos);

        builder.Services.AddMassTransit(x =>
        {
            x.AddConsumer<SolicitarDiagnosticoConsumer>()
                .Endpoint(e => e.Name = "execution-solicitar-diagnostico");
            x.AddConsumer<AuditoriaOrdemServicoAbertaConsumer>()
                .Endpoint(e => e.Name = "spike-auditoria-ordem-servico-aberta");
            x.AddConsumer<PingSemEntityNameConsumer>()
                .Endpoint(e => e.Name = "spike-ping-sem-entity-name");

            x.UsingRabbitMq((context, cfg) =>
            {
                ConfigurarHost(cfg);
                cfg.ConfigureEndpoints(context);
            });
        });

        return builder.Build();
    }

    private void ConfigurarHost(IRabbitMqBusFactoryConfigurator cfg) =>
        cfg.Host(_rabbit.Hostname, _rabbit.GetMappedPublicPort(5672), VHost, h =>
        {
            h.Username(RabbitUser);
            h.Password(RabbitPass);
        });

    private void ConfigurarLogs(HostApplicationBuilder builder, string nome)
    {
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new ColetorDeLogs(nome, Logs));
    }

    /// <summary>Nomes dos exchanges do vhost, pela API de management do RabbitMQ.</summary>
    public async Task<IReadOnlyList<string>> ListarExchangesAsync()
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://{_rabbit.Hostname}:{_rabbit.GetMappedPublicPort(15672)}")
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{RabbitUser}:{RabbitPass}")));

        var json = await http.GetStringAsync($"/api/exchanges/{VHost}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString() ?? string.Empty)
            .Where(n => n.Length > 0)
            .OrderBy(n => n)
            .ToList();
    }

    public async Task<string> LogsDoRabbitAsync()
    {
        var (stdout, stderr) = await _rabbit.GetLogsAsync();
        return stdout + stderr;
    }

    private sealed class ColetorDeLogs(string host, ConcurrentQueue<string> destino) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(host, categoryName, destino);
        public void Dispose() { }

        private sealed class Logger(string host, string categoria, ConcurrentQueue<string> destino) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                destino.Enqueue($"[{host}] {logLevel} {categoria}: {formatter(state, exception)} {exception?.GetType().Name} {exception?.Message}");
            }
        }
    }
}
