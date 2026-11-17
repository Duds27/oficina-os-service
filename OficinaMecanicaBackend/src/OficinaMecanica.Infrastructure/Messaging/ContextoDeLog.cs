using MassTransit;
using Oficina.Contracts;
using OficinaMecanica.Application.Saga;
using SerilogContext = Serilog.Context.LogContext;
using Serilog.Core;
using Serilog.Core.Enrichers;

namespace OficinaMecanica.Infrastructure.Messaging;

/// <summary>
/// Põe no LogContext do Serilog, durante o consumo, o tipo da mensagem e, nas mensagens da
/// saga, o CorrelationId e o NumeroOS. Todo log do consumer (e do MassTransit) sai com eles,
/// e filtrar os logs por NumeroOS no New Relic mostra a OS nos 3 serviços.
/// </summary>
public sealed class ContextoDeLogConsumeFilter<T> : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        using (SerilogContext.Push(Propriedades(context.Message)))
            await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("contextoDeLog");

    public static ILogEventEnricher[] Propriedades(T mensagem) => mensagem is IMensagemOS os
        ?
        [
            new PropertyEnricher("MessageType", typeof(T).Name),
            new PropertyEnricher("CorrelationId", os.CorrelationId),
            new PropertyEnricher("NumeroOS", os.NumeroOS)
        ]
        : [new PropertyEnricher("MessageType", typeof(T).Name)];
}

/// <summary>Acrescenta o estado da saga (antes da transição) aos logs do consumo da saga.</summary>
public sealed class EstadoSagaLogFilter : IFilter<SagaConsumeContext<OrdemServicoSagaState>>
{
    public async Task Send(SagaConsumeContext<OrdemServicoSagaState> context, IPipe<SagaConsumeContext<OrdemServicoSagaState>> next)
    {
        using (SerilogContext.PushProperty("EstadoSaga", context.Saga.CurrentState))
            await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("estadoSagaLog");
}
