using System.Collections.Concurrent;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Oficina.Contracts;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanicaBackend.Tests.Saga;

/// <summary>
/// Saga com o test harness do MassTransit: transporte e repositório em memória e as
/// operações na OS simuladas. Testa só a orquestração (estados, comandos e compensações).
/// </summary>
public sealed class SagaHarness : IAsyncDisposable
{
    public static readonly PrazosSagaOptions Prazos = new()
    {
        Aprovacao = TimeSpan.FromHours(48),
        Pagamento = TimeSpan.FromHours(24),
        Execucao = TimeSpan.FromHours(12)
    };

    private readonly ServiceProvider _provider;

    private SagaHarness(ServiceProvider provider, FakeOperacoesOsSaga ops)
    {
        _provider = provider;
        Ops = ops;
        Harness = provider.GetRequiredService<ITestHarness>();
        Saga = Harness.GetSagaStateMachineHarness<OrdemServicoStateMachine, OrdemServicoSagaState>();
    }

    public ITestHarness Harness { get; }
    public ISagaStateMachineTestHarness<OrdemServicoStateMachine, OrdemServicoSagaState> Saga { get; }
    public OrdemServicoStateMachine Maquina => Saga.StateMachine;
    public FakeOperacoesOsSaga Ops { get; }

    public Guid CorrelationId { get; } = Guid.NewGuid();
    public const int OsId = 42;
    public const string NumeroOS = "OS-2026-000042";
    public static readonly Guid OrcamentoId = Guid.NewGuid();
    public static readonly Guid PagamentoId = Guid.NewGuid();

    public static async Task<SagaHarness> IniciarAsync()
    {
        var ops = new FakeOperacoesOsSaga();
        var provider = new ServiceCollection()
            .AddSingleton<IOperacoesOsSaga>(ops)
            .Configure<PrazosSagaOptions>(o =>
            {
                o.Aprovacao = Prazos.Aprovacao;
                o.Pagamento = Prazos.Pagamento;
                o.Execucao = Prazos.Execucao;
            })
            .AddMassTransitTestHarness(x =>
            {
                x.AddSagaStateMachine<OrdemServicoStateMachine, OrdemServicoSagaState>().InMemoryRepository();
                // As esperas dos testes são próprias (PublicarAsync); a inatividade só atrasa o Stop.
                x.SetTestTimeouts(testInactivityTimeout: TimeSpan.FromMilliseconds(200));
            })
            .BuildServiceProvider(validateScopes: true);

        var sagaHarness = new SagaHarness(provider, ops);
        await sagaHarness.Harness.Start();
        return sagaHarness;
    }

    public async ValueTask DisposeAsync()
    {
        await Harness.Stop();
        await _provider.DisposeAsync();
    }

    /// <summary>Mensagem com o envelope desta OS.</summary>
    public T Msg<T>() where T : MensagemOS, new() => new()
    {
        CorrelationId = CorrelationId,
        OsId = OsId,
        NumeroOS = NumeroOS,
        OcorridoEm = DateTime.UtcNow
    };

    /// <summary>Publica e espera a saga consumir a mensagem, sem exceção.</summary>
    public Task PublicarAsync<T>(T mensagem) where T : class => PublicarAsync(mensagem, Saga.Consumed);

    /// <summary>
    /// Publica e espera o bus consumir, sem exceção. Para eventos que nem chegam à saga
    /// (saga inexistente: o MassTransit descarta antes).
    /// </summary>
    public Task PublicarNoBusAsync<T>(T mensagem) where T : class => PublicarAsync(mensagem, Harness.Consumed);

    private async Task PublicarAsync<T>(T mensagem, IReceivedMessageList consumidas) where T : class
    {
        var messageId = NewId.NextGuid();
        await Harness.Bus.Publish(mensagem, c => c.MessageId = messageId);

        // Espera própria: o Consumed.Any do harness deixa de esperar depois da primeira
        // janela de inatividade do bus, e os testes publicam várias mensagens em sequência.
        var limite = DateTime.UtcNow + Espera;
        IReceivedMessage<T>? consumida;
        while ((consumida = consumidas.Select<T>(m => m.Context.MessageId == messageId).FirstOrDefault()) is null)
        {
            if (DateTime.UtcNow > limite)
                Assert.Fail($"{typeof(T).Name} não foi consumida em {Espera.TotalSeconds} s.");
            await Task.Delay(10);
        }
        Assert.Null(consumida.Exception);
        Assert.False(Harness.Published.Select<Fault<T>>().Any(), $"{typeof(T).Name} gerou Fault.");
    }

    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(10);

    /// <summary>Espera a saga chegar ao estado e devolve a instância.</summary>
    public async Task<OrdemServicoSagaState> EsperarEstadoAsync(Func<OrdemServicoStateMachine, State> estado)
    {
        var id = await Saga.Exists(CorrelationId, estado, Espera);
        Assert.True(id.HasValue, $"A saga não chegou a {estado(Maquina).Name}; está em {EstadoAtual}.");
        return Saga.Sagas.Contains(CorrelationId)!;
    }

    public string EstadoAtual => Saga.Sagas.Contains(CorrelationId)!.CurrentState;

    // ───────────── Caminho feliz até cada estado ─────────────

    public async Task<OrdemServicoSagaState> AbrirAsync()
    {
        await PublicarAsync(Msg<OrdemServicoAberta>());
        return await EsperarEstadoAsync(m => m.AguardandoDiagnostico);
    }

    public async Task<OrdemServicoSagaState> AteEmDiagnosticoAsync()
    {
        await AbrirAsync();
        await PublicarAsync(Msg<DiagnosticoIniciado>() with { Mecanico = "Carlos" });
        return await EsperarEstadoAsync(m => m.EmDiagnostico);
    }

    public async Task<OrdemServicoSagaState> AteAguardandoOrcamentoAsync()
    {
        await AteEmDiagnosticoAsync();
        await PublicarAsync(Msg<DiagnosticoConcluido>());
        return await EsperarEstadoAsync(m => m.AguardandoOrcamento);
    }

    public async Task<OrdemServicoSagaState> AteAguardandoAprovacaoAsync()
    {
        await AteAguardandoOrcamentoAsync();
        await PublicarAsync(Msg<OrcamentoGerado>() with { OrcamentoId = OrcamentoId, ValorTotal = 165.90m, ValidoAte = DateTime.UtcNow.AddDays(2) });
        return await EsperarEstadoAsync(m => m.AguardandoAprovacao);
    }

    public async Task<OrdemServicoSagaState> AteAguardandoPagamentoAsync()
    {
        await AteAguardandoAprovacaoAsync();
        await PublicarAsync(Msg<OrcamentoAprovado>() with { OrcamentoId = OrcamentoId, PagamentoId = PagamentoId, LinkPagamento = "https://mp.example/checkout/1" });
        return await EsperarEstadoAsync(m => m.AguardandoPagamento);
    }

    public async Task<OrdemServicoSagaState> AteAguardandoExecucaoAsync()
    {
        await AteAguardandoPagamentoAsync();
        await PublicarAsync(Msg<PagamentoConfirmado>() with { PagamentoId = PagamentoId, MpPaymentId = "123456789", Valor = 165.90m });
        return await EsperarEstadoAsync(m => m.AguardandoExecucao);
    }

    public async Task<OrdemServicoSagaState> AteEmExecucaoAsync()
    {
        await AteAguardandoExecucaoAsync();
        await PublicarAsync(Msg<ExecucaoIniciada>());
        return await EsperarEstadoAsync(m => m.EmExecucao);
    }

    // ───────────── Comandos enviados ─────────────

    /// <summary>Comandos do tipo T enviados até agora (sem esperar).</summary>
    public IReadOnlyList<T> Enviados<T>() where T : class =>
        Harness.Sent.Select<T>().Select(m => m.Context.Message).ToList();

    public IReadOnlyList<Uri?> DestinosDe<T>() where T : class =>
        Harness.Sent.Select<T>().Select(m => m.Context.DestinationAddress).ToList();
}

/// <summary>Operações na OS simuladas: registram o que a saga pediu.</summary>
public sealed class FakeOperacoesOsSaga : IOperacoesOsSaga
{
    public ConcurrentQueue<(StatusOrdemServico Alvo, string Motivo)> Status { get; } = new();
    private int _devolucoes;
    public int Devolucoes => _devolucoes;

    public DadosOsSaga Dados { get; } = new(
        SagaHarness.OsId,
        SagaHarness.NumeroOS,
        "Barulho no motor",
        new VeiculoInfo { Placa = "ABC1D23", Marca = "Ford", Modelo = "Ka", Ano = 2020 },
        new ClienteInfo { Id = 7, Nome = "Maria Souza", Email = "maria@example.com" },
        [
            new ItemOrcamentoInfo { Tipo = "Servico", ReferenciaId = 3, Descricao = "Troca de óleo", Quantidade = 1, PrecoUnitario = 120m },
            new ItemOrcamentoInfo { Tipo = "Peca", ReferenciaId = 15, Descricao = "Filtro de óleo", Quantidade = 1, PrecoUnitario = 45.90m }
        ],
        165.90m,
        [new ServicoExecucaoInfo { Descricao = "Troca de óleo", Quantidade = 1 }]);

    public IReadOnlyList<StatusOrdemServico> Alvos => Status.Select(s => s.Alvo).ToList();

    public Task<DadosOsSaga> ObterDadosAsync(int osId, CancellationToken ct = default) => Task.FromResult(Dados);

    public Task AplicarStatusAsync(int osId, StatusOrdemServico alvo, string motivo, CancellationToken ct = default)
    {
        Status.Enqueue((alvo, motivo));
        return Task.CompletedTask;
    }

    public Task DevolverPecasAsync(int osId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _devolucoes);
        return Task.CompletedTask;
    }
}
