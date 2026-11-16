using OficinaMecanica.Application.Abstractions;
using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanicaBackend.Tests.Services;

/// <summary>Notificador de teste que apenas registra as chamadas, sem I/O.</summary>
internal sealed class FakeNotificador : INotificadorStatus
{
    public List<(StatusOrdemServico Anterior, StatusOrdemServico Novo)> Notificacoes { get; } = [];

    public Task NotificarMudancaStatusAsync(
        OrdemServico ordem, StatusOrdemServico statusAnterior, StatusOrdemServico statusNovo,
        CancellationToken ct = default)
    {
        Notificacoes.Add((statusAnterior, statusNovo));
        return Task.CompletedTask;
    }
}

/// <summary>Publicador de teste: guarda os eventos em memória, sem broker.</summary>
internal sealed class FakePublicador : IPublicadorEventos
{
    public List<object> Publicados { get; } = [];

    public Task PublicarAsync<T>(T evento, CancellationToken ct = default) where T : class
    {
        Publicados.Add(evento);
        return Task.CompletedTask;
    }
}
