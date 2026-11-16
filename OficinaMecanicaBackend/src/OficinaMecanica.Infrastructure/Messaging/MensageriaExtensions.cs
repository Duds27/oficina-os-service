using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OficinaMecanica.Application.Abstractions;
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

        services.AddMassTransit(x =>
        {
            x.SetKebabCaseEndpointNameFormatter();

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

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
