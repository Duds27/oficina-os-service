using System.Diagnostics.Metrics;

namespace OficinaMecanica.Application.Saga;

/// <summary>
/// Métricas de negócio da saga (Meter <c>Oficina.Saga</c>), exportadas pelo OpenTelemetry.
/// Só usa System.Diagnostics.Metrics: a camada de aplicação não depende do exportador.
/// </summary>
/// <remarks>
/// As medições acontecem dentro do consumo da mensagem. Se a transação falhar e o
/// MassTransit tentar de novo, a mesma transição pode ser contada duas vezes: é um desvio
/// aceitável para métricas (o estado da saga, no banco, continua sendo a fonte de verdade).
/// </remarks>
public static class MetricasSaga
{
    public const string NomeMeter = "Oficina.Saga";

    private static readonly Meter Meter = new(NomeMeter, "1.0.0");
    private static long _prazosAtrasados;

    public static readonly Counter<long> Iniciada =
        Meter.CreateCounter<long>("saga.iniciada", unit: "{saga}", description: "Sagas de OS iniciadas (OS aberta).");

    public static readonly Counter<long> Concluida =
        Meter.CreateCounter<long>("saga.concluida", unit: "{saga}", description: "Sagas que chegaram a Concluida.");

    /// <summary>Tag <c>motivo</c>: o evento que disparou a compensação (ex.: OrcamentoRecusado).</summary>
    public static readonly Counter<long> Compensada =
        Meter.CreateCounter<long>("saga.compensada", unit: "{saga}", description: "Sagas compensadas, por motivo.");

    /// <summary>Tag <c>etapa</c>: o estado da saga que terminou.</summary>
    public static readonly Histogram<double> DuracaoEtapa =
        Meter.CreateHistogram<double>("saga.etapa.duracao", unit: "s", description: "Tempo que a saga passou em cada estado.");

    static MetricasSaga()
    {
        Meter.CreateObservableGauge(
            "saga.prazos.atrasados",
            () => Interlocked.Read(ref _prazosAtrasados),
            unit: "{saga}",
            description: "Sagas com prazo vencido há mais de duas rodadas do verificador sem PrazoExpirado.");
    }

    /// <summary>Atualizado pelo VerificadorPrazosSaga a cada rodada.</summary>
    public static void RegistrarPrazosAtrasados(long quantidade) => Interlocked.Exchange(ref _prazosAtrasados, quantidade);

    public static long PrazosAtrasados => Interlocked.Read(ref _prazosAtrasados);
}
