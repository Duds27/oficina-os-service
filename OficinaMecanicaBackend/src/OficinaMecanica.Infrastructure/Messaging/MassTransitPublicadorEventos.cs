using MassTransit;
using OficinaMecanica.Application.Abstractions;

namespace OficinaMecanica.Infrastructure.Messaging;

/// <summary>
/// Publica pelo outbox do MassTransit (bus outbox): o <see cref="IPublishEndpoint"/>
/// do escopo grava a mensagem no mesmo AppDbContext, e ela só sai depois do SaveChanges.
/// </summary>
public class MassTransitPublicadorEventos : IPublicadorEventos
{
    private readonly IPublishEndpoint _publish;

    public MassTransitPublicadorEventos(IPublishEndpoint publish) => _publish = publish;

    public Task PublicarAsync<T>(T evento, CancellationToken ct = default) where T : class =>
        _publish.Publish(evento, ct);
}
