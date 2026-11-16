using System.Collections.Concurrent;
using MassTransit;
using OficinaMecanica.Application.Abstractions;
using OficinaMecanica.Infrastructure.Data;

namespace OficinaMecanica.BddTests.Suporte;

/// <summary>
/// Faz o papel do repositório EF da saga: grava, no fim do consumo, o que a saga mudou no
/// AppDbContext do escopo da mensagem (OS, histórico, estoque, notificação). Em produção
/// quem salva é o repositório da saga, na transação do outbox.
/// </summary>
public class SalvarDbContextFilter<T>(AppDbContext db) : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        await next.Send(context);
        await db.SaveChangesAsync(context.CancellationToken);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("salvar-db-context");
}

/// <summary>
/// Faz o papel do outbox na API: guarda os eventos e só entrega ao bus depois que o caso de
/// uso terminou (commit). Publicar antes faria a saga procurar uma OS ainda não gravada.
/// </summary>
public sealed class PublicadorAposCommit : IPublicadorEventos
{
    private readonly ConcurrentQueue<object> _pendentes = new();

    public Task PublicarAsync<T>(T evento, CancellationToken ct = default) where T : class
    {
        _pendentes.Enqueue(evento);
        return Task.CompletedTask;
    }

    public async Task EntregarAsync(IBus bus)
    {
        while (_pendentes.TryDequeue(out var evento))
            await bus.Publish(evento, evento.GetType());
    }
}
