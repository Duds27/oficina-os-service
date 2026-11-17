using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Oficina.Contracts;
using OficinaMecanica.Application.Saga;

namespace OficinaMecanicaBackend.Tests.Saga;

/// <summary>
/// O Meter da saga é estático: outra classe de teste que rodasse a saga em paralelo somaria nas
/// contagens. Esta coleção roda sozinha.
/// </summary>
[CollectionDefinition(nameof(MetricasSagaColecao), DisableParallelization = true)]
public class MetricasSagaColecao;

/// <summary>Métricas de negócio da saga (Meter Oficina.Saga, Etapa 5).</summary>
[Collection(nameof(MetricasSagaColecao))]
public class MetricasSagaTests : IAsyncLifetime
{
    private readonly ColetorDeMetricas _metricas = new(MetricasSaga.NomeMeter);
    private SagaHarness _s = null!;

    public async Task InitializeAsync() => _s = await SagaHarness.IniciarAsync();

    public async Task DisposeAsync()
    {
        await _s.DisposeAsync();
        _metricas.Dispose();
    }

    [Fact]
    public async Task Abertura_ContaUmaSagaIniciada()
    {
        await _s.AbrirAsync();

        Assert.Equal(1, _metricas.Soma("saga.iniciada"));
        Assert.Equal(0, _metricas.Soma("saga.concluida"));
    }

    [Fact]
    public async Task CaminhoFeliz_ContaAConclusaoEMedeOTempoDeCadaEstado()
    {
        await _s.AteEmExecucaoAsync();
        await _s.PublicarAsync(_s.Msg<ExecucaoFinalizada>() with { DuracaoSegundos = 3600 });
        await _s.EsperarEstadoAsync(m => m.Concluida);

        Assert.Equal(1, _metricas.Soma("saga.concluida"));
        Assert.Equal(0, _metricas.Soma("saga.compensada"));

        var etapas = _metricas.Medicoes("saga.etapa.duracao").Select(m => m.Tags["etapa"]).ToList();
        Assert.Equal(
            ["AguardandoDiagnostico", "EmDiagnostico", "AguardandoOrcamento", "AguardandoAprovacao",
             "AguardandoPagamento", "AguardandoExecucao", "EmExecucao"],
            etapas);
        Assert.All(_metricas.Medicoes("saga.etapa.duracao"), m => Assert.True(m.Valor >= 0));
    }

    [Fact]
    public async Task Recusa_ContaUmaCompensacaoComOEventoComoMotivo()
    {
        await _s.AteAguardandoAprovacaoAsync();
        await _s.PublicarAsync(_s.Msg<OrcamentoRecusado>() with { OrcamentoId = SagaHarness.OrcamentoId, Motivo = "Caro demais" });
        await _s.EsperarEstadoAsync(m => m.Cancelada);

        var compensacao = Assert.Single(_metricas.Medicoes("saga.compensada"));
        Assert.Equal("OrcamentoRecusado", compensacao.Tags["motivo"]);
        Assert.Equal(0, _metricas.Soma("saga.concluida"));
    }

    [Fact]
    public async Task PrazoExpirado_LevaOEstadoNoMotivo()
    {
        await _s.AteAguardandoPagamentoAsync();
        await _s.PublicarAsync(_s.Msg<PrazoExpirado>() with { EstadoNoMomento = "AguardandoPagamento" });
        await _s.EsperarEstadoAsync(m => m.Cancelada);

        var compensacao = Assert.Single(_metricas.Medicoes("saga.compensada"));
        Assert.Equal("PrazoExpirado.AguardandoPagamento", compensacao.Tags["motivo"]);
    }

    [Fact]
    public void PrazosAtrasados_SaiNoGaugeComOUltimoValor()
    {
        MetricasSaga.RegistrarPrazosAtrasados(3);
        try
        {
            Assert.Equal(3, _metricas.Observar("saga.prazos.atrasados"));
        }
        finally
        {
            MetricasSaga.RegistrarPrazosAtrasados(0);
        }
    }
}

/// <summary>Coleta, em memória, as medições de um Meter (MeterListener).</summary>
public sealed class ColetorDeMetricas : IDisposable
{
    public sealed record Medicao(string Instrumento, double Valor, IReadOnlyDictionary<string, string?> Tags);

    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Medicao> _medicoes = new();

    public ColetorDeMetricas(string nomeMeter)
    {
        _listener.InstrumentPublished = (instrumento, listener) =>
        {
            if (instrumento.Meter.Name == nomeMeter)
                listener.EnableMeasurementEvents(instrumento);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Guardar(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Guardar(i, v, tags));
        _listener.Start();
    }

    public IReadOnlyList<Medicao> Medicoes(string instrumento) =>
        _medicoes.Where(m => m.Instrumento == instrumento).ToList();

    public double Soma(string instrumento) => Medicoes(instrumento).Sum(m => m.Valor);

    /// <summary>Lê os instrumentos observáveis (gauges) agora e devolve o último valor do pedido.</summary>
    public double Observar(string instrumento)
    {
        _listener.RecordObservableInstruments();
        return Medicoes(instrumento)[^1].Valor;
    }

    private void Guardar(Instrument instrumento, double valor, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dicionario = new Dictionary<string, string?>();
        foreach (var tag in tags)
            dicionario[tag.Key] = tag.Value?.ToString();
        _medicoes.Enqueue(new Medicao(instrumento.Name, valor, dicionario));
    }

    public void Dispose() => _listener.Dispose();
}
