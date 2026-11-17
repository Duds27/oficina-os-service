using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oficina.Contracts;
using OficinaMecanica.Application.Abstractions;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Domain.Exceptions;
using OficinaMecanica.Infrastructure.Data;

namespace OficinaMecanica.Infrastructure.Messaging;

public static class MensageriaExtensions
{
    /// <summary>
    /// MassTransit 8.4.1 + RabbitMQ, com outbox e inbox no MySQL (AppDbContext).
    /// Decisões em docs/architecture/adrs/ADR-007-rabbitmq-masstransit.md.
    /// </summary>
    public static IServiceCollection AddMensageria(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IPublicadorEventos, MassTransitPublicadorEventos>();

        // Prazos da saga e o verificador que publica PrazoExpirado.
        services.Configure<PrazosSagaOptions>(configuration.GetSection(PrazosSagaOptions.Secao));
        services.Configure<VerificadorPrazosSagaOptions>(configuration.GetSection(VerificadorPrazosSagaOptions.Secao));
        services.AddSingleton<VerificadorPrazosSaga>();
        services.AddHostedService<VerificadorPrazosSagaHostedService>();

        services.AddMassTransit(x =>
        {
            x.SetKebabCaseEndpointNameFormatter();

            // Saga orquestrada da OS, persistida no MySQL com concorrência otimista, no
            // mesmo AppDbContext da OS: estado da saga e mudanças na OS na mesma transação.
            // O filtro põe o estado da saga nos logs do consumo (EstadoSaga).
            x.AddSagaStateMachine<OrdemServicoStateMachine, OrdemServicoSagaState>((_, saga) => saga.UseFilter(new EstadoSagaLogFilter()))
                .UsarRepositorioMySql()
                .Endpoint(e => e.Name = FilasOs.Saga);

            // Publicações fora de um consumidor (ex.: abertura da OS na API) vão para a
            // tabela OutboxMessage na mesma transação e saem depois do commit.
            x.AddEntityFrameworkOutbox<AppDbContext>(o =>
            {
                o.UseMySql();
                o.UseBusOutbox();
            });

            x.AddConfigureEndpointsCallback((context, _, endpoint) =>
            {
                // Falha transitória (broker, banco, concorrência otimista da saga): novas
                // tentativas com intervalo crescente. Esgotou, a mensagem vai para <fila>_error.
                // O redelivery atrasado exige o plugin rabbitmq_delayed_message_exchange,
                // que a imagem padrão não tem; fica para a Etapa 4 (Cluster Operator).
                endpoint.UseMessageRetry(r =>
                {
                    r.Exponential(5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(500));
                    // Erro de regra não se resolve tentando de novo.
                    r.Ignore<TransicaoStatusInvalidaException>();
                });

                // Inbox (descarta mensagem repetida pelo MessageId) + outbox das mensagens
                // enviadas pelo consumidor, na mesma transação das mudanças no banco.
                endpoint.UseEntityFrameworkOutbox<AppDbContext>(context);
            });

            x.ConfigureHealthCheckOptions(o =>
            {
                o.Name = "masstransit";
                o.MinimalFailureStatus = HealthStatus.Unhealthy;
                o.Tags.Add("ready");
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbit = configuration.GetSection(RabbitMqOptions.Secao).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
                cfg.Host(rabbit.Host, rabbit.Port, rabbit.VirtualHost, h =>
                {
                    h.Username(rabbit.Username);
                    h.Password(rabbit.Password);
                });

                // CorrelationId, NumeroOS e MessageType nos logs de todo consumo (ContextoDeLog).
                cfg.UseConsumeFilter(typeof(ContextoDeLogConsumeFilter<>), context);

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    /// <summary>
    /// Repositório da saga no AppDbContext (MySQL), com concorrência otimista pelo RowVersion.
    /// Público para os testes de integração: o test harness do MassTransit troca os
    /// repositórios de saga por memória, e os testes registram este de volta.
    /// </summary>
    public static ISagaRegistrationConfigurator<OrdemServicoSagaState> UsarRepositorioMySql(
        this ISagaRegistrationConfigurator<OrdemServicoSagaState> saga) =>
        saga.EntityFrameworkRepository(r =>
        {
            r.ConcurrencyMode = ConcurrencyMode.Optimistic;
            r.ExistingDbContext<AppDbContext>();
            r.UseMySql();
        });
}
