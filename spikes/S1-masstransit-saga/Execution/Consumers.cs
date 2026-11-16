using System.Collections.Concurrent;
using MassTransit;
using Spike.Execution.Contracts;
using PingSemEntityName = Spike.Execution.Contracts.SemEntityName.Ping;

namespace Spike.Execution;

/// <summary>O que o "execution-service" recebeu, para os testes conferirem.</summary>
public class Recebidos
{
    public ConcurrentDictionary<Guid, SolicitarDiagnostico> Diagnosticos { get; } = new();
    public ConcurrentDictionary<Guid, OrdemServicoAberta> OrdensAbertas { get; } = new();
    public ConcurrentDictionary<Guid, bool> Pings { get; } = new();
}

/// <summary>Simula o execution-service: recebe o comando e responde com o evento.</summary>
public class SolicitarDiagnosticoConsumer(Recebidos recebidos) : IConsumer<SolicitarDiagnostico>
{
    public async Task Consume(ConsumeContext<SolicitarDiagnostico> context)
    {
        var m = context.Message;
        recebidos.Diagnosticos[m.CorrelationId] = m;

        await context.Publish(new DiagnosticoIniciado
        {
            CorrelationId = m.CorrelationId,
            OsId = m.OsId,
            NumeroOS = m.NumeroOS,
            OcorridoEm = DateTime.UtcNow,
            Mecanico = "mecanico.spike"
        });
    }
}

/// <summary>Sonda: assina o evento publicado pelo outbox do os-service.</summary>
public class AuditoriaOrdemServicoAbertaConsumer(Recebidos recebidos) : IConsumer<OrdemServicoAberta>
{
    public Task Consume(ConsumeContext<OrdemServicoAberta> context)
    {
        recebidos.OrdensAbertas[context.Message.CorrelationId] = context.Message;
        return Task.CompletedTask;
    }
}

public class PingSemEntityNameConsumer(Recebidos recebidos) : IConsumer<PingSemEntityName>
{
    public Task Consume(ConsumeContext<PingSemEntityName> context)
    {
        recebidos.Pings[context.Message.Id] = true;
        return Task.CompletedTask;
    }
}
