using System.Collections.Concurrent;
using MassTransit;
using Oficina.Contracts;

namespace OficinaMecanica.BddTests.Suporte;

/// <summary>
/// O billing-service e o execution-service simulados: recebem os comandos da saga nas
/// filas reais de cada um e respondem como o serviço responderia. O comportamento muda
/// por cenário (ex.: rejeitar a execução).
/// </summary>
public sealed class ServicosSimulados
{
    private readonly ConcurrentQueue<object> _comandos = new();

    /// <summary>Faz o execution-service responder ExecucaoRejeitada ao IniciarExecucao.</summary>
    public bool RejeitarExecucao { get; set; }

    /// <summary>Último orçamento gerado pelo billing simulado.</summary>
    public Guid? OrcamentoId { get; set; }

    public void Registrar(object comando) => _comandos.Enqueue(comando);

    public IReadOnlyList<T> Recebidos<T>() => _comandos.OfType<T>().ToList();

    public static T Resposta<T>(MensagemOS origem) where T : MensagemOS, new() => new()
    {
        CorrelationId = origem.CorrelationId,
        OsId = origem.OsId,
        NumeroOS = origem.NumeroOS,
        OcorridoEm = DateTime.UtcNow
    };
}

// ───────────── billing-service ─────────────

public class GerarOrcamentoSimulado(ServicosSimulados simulados) : IConsumer<GerarOrcamento>
{
    public async Task Consume(ConsumeContext<GerarOrcamento> context)
    {
        simulados.Registrar(context.Message);
        simulados.OrcamentoId = Guid.NewGuid();
        // O billing gera o orçamento e envia ao cliente.
        await context.Publish(ServicosSimulados.Resposta<OrcamentoGerado>(context.Message) with
        {
            OrcamentoId = simulados.OrcamentoId.Value,
            ValorTotal = context.Message.ValorTotal,
            ValidoAte = DateTime.UtcNow.AddDays(2)
        });
    }
}

public class CancelarOrcamentoSimulado(ServicosSimulados simulados) : IConsumer<CancelarOrcamento>
{
    public Task Consume(ConsumeContext<CancelarOrcamento> context)
    {
        simulados.Registrar(context.Message);
        return Task.CompletedTask;
    }
}

public class EstornarPagamentoSimulado(ServicosSimulados simulados) : IConsumer<EstornarPagamento>
{
    public async Task Consume(ConsumeContext<EstornarPagamento> context)
    {
        simulados.Registrar(context.Message);
        // O billing estorna no Mercado Pago e confirma.
        await context.Publish(ServicosSimulados.Resposta<PagamentoEstornado>(context.Message) with
        {
            PagamentoId = context.Message.PagamentoId,
            Valor = 0m
        });
    }
}

// ───────────── execution-service ─────────────

public class SolicitarDiagnosticoSimulado(ServicosSimulados simulados) : IConsumer<SolicitarDiagnostico>
{
    // A OS entra na fila do mecânico; o diagnóstico é iniciado e concluído pelos passos do cenário.
    public Task Consume(ConsumeContext<SolicitarDiagnostico> context)
    {
        simulados.Registrar(context.Message);
        return Task.CompletedTask;
    }
}

public class IniciarExecucaoSimulado(ServicosSimulados simulados) : IConsumer<IniciarExecucao>
{
    public async Task Consume(ConsumeContext<IniciarExecucao> context)
    {
        simulados.Registrar(context.Message);
        if (simulados.RejeitarExecucao)
            await context.Publish(ServicosSimulados.Resposta<ExecucaoRejeitada>(context.Message) with
            {
                Motivo = "Sem mecânico disponível"
            });
        else
            await context.Publish(ServicosSimulados.Resposta<ExecucaoIniciada>(context.Message));
    }
}

public class CancelarExecucaoSimulado(ServicosSimulados simulados) : IConsumer<CancelarExecucao>
{
    public Task Consume(ConsumeContext<CancelarExecucao> context)
    {
        simulados.Registrar(context.Message);
        return Task.CompletedTask;
    }
}
