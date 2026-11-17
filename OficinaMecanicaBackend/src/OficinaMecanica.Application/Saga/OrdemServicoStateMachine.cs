using System.Linq.Expressions;
using MassTransit;
using Microsoft.Extensions.Options;
using Oficina.Contracts;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Application.Saga;

/// <summary>
/// Saga orquestrada da OS (ADR-006). Diagrama, compensações e regras de eventos fora de
/// ordem: docs/contratos-mensagens.md, seção 4.
///
/// Cada mensagem é processada numa única transação do AppDbContext: estado da saga, status
/// e histórico da OS, estoque, notificação e comandos de saída (outbox) são gravados juntos.
/// </summary>
public class OrdemServicoStateMachine : MassTransitStateMachine<OrdemServicoSagaState>
{
    private readonly PrazosSagaOptions _prazos;

    // ───────────── Estados (seção 4 do documento de contratos) ─────────────
    public State AguardandoDiagnostico { get; private set; } = null!;
    public State EmDiagnostico { get; private set; } = null!;
    public State AguardandoOrcamento { get; private set; } = null!;
    public State AguardandoAprovacao { get; private set; } = null!;
    public State AguardandoPagamento { get; private set; } = null!;
    public State AguardandoExecucao { get; private set; } = null!;
    public State EmExecucao { get; private set; } = null!;
    public State Concluida { get; private set; } = null!;
    public State AguardandoEstorno { get; private set; } = null!;
    public State Cancelada { get; private set; } = null!;

    // ───────────── Eventos que a saga consome ─────────────
    public Event<OrdemServicoAberta> OrdemServicoAberta { get; private set; } = null!;
    public Event<DiagnosticoIniciado> DiagnosticoIniciado { get; private set; } = null!;
    public Event<DiagnosticoConcluido> DiagnosticoConcluido { get; private set; } = null!;
    public Event<OrcamentoGerado> OrcamentoGerado { get; private set; } = null!;
    public Event<OrcamentoAprovado> OrcamentoAprovado { get; private set; } = null!;
    public Event<OrcamentoRecusado> OrcamentoRecusado { get; private set; } = null!;
    public Event<PagamentoConfirmado> PagamentoConfirmado { get; private set; } = null!;
    public Event<PagamentoRecusado> PagamentoRecusado { get; private set; } = null!;
    public Event<ExecucaoIniciada> ExecucaoIniciada { get; private set; } = null!;
    public Event<ExecucaoRejeitada> ExecucaoRejeitada { get; private set; } = null!;
    public Event<ExecucaoFinalizada> ExecucaoFinalizada { get; private set; } = null!;
    public Event<PagamentoEstornado> PagamentoEstornado { get; private set; } = null!;
    public Event<PrazoExpirado> PrazoExpirado { get; private set; } = null!;
    public Event<CancelamentoSolicitado> CancelamentoSolicitado { get; private set; } = null!;

    public OrdemServicoStateMachine(IOptions<PrazosSagaOptions> prazos)
    {
        _prazos = prazos.Value;

        InstanceState(x => x.CurrentState);

        Event(() => OrdemServicoAberta, e => e.CorrelateById(c => c.Message.CorrelationId));
        Correlacionar(() => DiagnosticoIniciado);
        Correlacionar(() => DiagnosticoConcluido);
        Correlacionar(() => OrcamentoGerado);
        Correlacionar(() => OrcamentoAprovado);
        Correlacionar(() => OrcamentoRecusado);
        Correlacionar(() => PagamentoConfirmado);
        Correlacionar(() => PagamentoRecusado);
        Correlacionar(() => ExecucaoIniciada);
        Correlacionar(() => ExecucaoRejeitada);
        Correlacionar(() => ExecucaoFinalizada);
        Correlacionar(() => PagamentoEstornado);
        Correlacionar(() => PrazoExpirado);
        Correlacionar(() => CancelamentoSolicitado);

        // ───────────── Métricas (MetricasSaga) ─────────────
        // Ao sair de um estado, registra quanto tempo a saga ficou nele. Entrar num estado
        // marca o início da próxima medição. O Initial do MassTransit não é uma etapa da OS e
        // fica de fora.
        WhenLeaveAny(x => x.Then(RegistrarDuracaoDoEstado));
        WhenEnterAny(x => x.Then(c => c.Saga.EstadoDesde = DateTime.UtcNow));
        WhenEnter(Concluida, x => x.Then(_ => MetricasSaga.Concluida.Add(1)));

        // ───────────── Abertura → diagnóstico ─────────────
        Initially(
            When(OrdemServicoAberta)
                .Then(c =>
                {
                    c.Saga.OsId = c.Message.OsId;
                    c.Saga.NumeroOS = c.Message.NumeroOS;
                    c.Saga.CriadoEm = DateTime.UtcNow;
                    MetricasSaga.Iniciada.Add(1);
                })
                .ThenAsync(async c =>
                {
                    var os = await Ops(c).ObterDadosAsync(c.Saga.OsId, c.CancellationToken);
                    await Enviar(c, FilasComando.SolicitarDiagnostico,
                        Mensagem<SolicitarDiagnostico>(c.Saga) with { Veiculo = os.Veiculo, Descricao = os.Descricao });
                })
                .TransitionTo(AguardandoDiagnostico));

        During(AguardandoDiagnostico,
            When(DiagnosticoIniciado)
                .ThenAsync(c => Status(c, StatusOrdemServico.EmDiagnostico, $"Diagnóstico iniciado por {c.Message.Mecanico}"))
                .Then(Atualizado)
                .TransitionTo(EmDiagnostico),
            // Fora de ordem: a conclusão chegou antes do aviso de início.
            When(DiagnosticoConcluido)
                .ThenAsync(c => Status(c, StatusOrdemServico.EmDiagnostico, "Diagnóstico concluído"))
                .ThenAsync(SolicitarOrcamento)
                .Then(Atualizado)
                .TransitionTo(AguardandoOrcamento),
            When(CancelamentoSolicitado)
                .ThenAsync(c => CompensarAntesDoOrcamento(c, MotivoDoCancelamento(c.Message)))
                .TransitionTo(Cancelada));

        During(EmDiagnostico,
            When(DiagnosticoConcluido)
                .ThenAsync(SolicitarOrcamento)
                .Then(Atualizado)
                .TransitionTo(AguardandoOrcamento),
            When(CancelamentoSolicitado)
                .ThenAsync(c => CompensarAntesDoOrcamento(c, MotivoDoCancelamento(c.Message)))
                .TransitionTo(Cancelada));

        // ───────────── Orçamento e aprovação ─────────────
        During(AguardandoOrcamento,
            When(OrcamentoGerado)
                .Then(c =>
                {
                    c.Saga.OrcamentoId = c.Message.OrcamentoId;
                    c.Saga.PrazoExpiraEm = DateTime.UtcNow + _prazos.Aprovacao;
                })
                .ThenAsync(c => Status(c, StatusOrdemServico.AguardandoAprovacao, "Orçamento enviado ao cliente"))
                .Then(Atualizado)
                .TransitionTo(AguardandoAprovacao),
            // O GerarOrcamento já foi enviado: o billing também precisa cancelar.
            When(CancelamentoSolicitado)
                .ThenAsync(c => CompensarAntesDaExecucao(c, MotivoDoCancelamento(c.Message)))
                .TransitionTo(Cancelada));

        During(AguardandoAprovacao,
            When(OrcamentoAprovado)
                .Then(c =>
                {
                    c.Saga.PagamentoId = c.Message.PagamentoId;
                    c.Saga.LinkPagamento = c.Message.LinkPagamento;
                    c.Saga.PrazoExpiraEm = DateTime.UtcNow + _prazos.Pagamento;
                })
                .ThenAsync(c => Status(c, StatusOrdemServico.AguardandoPagamento, "Orçamento aprovado pelo cliente"))
                .Then(Atualizado)
                .TransitionTo(AguardandoPagamento),
            // C1
            When(OrcamentoRecusado)
                .ThenAsync(c => CompensarRecusa(c, "Orçamento recusado pelo cliente" + Detalhe(c.Message.Motivo)))
                .TransitionTo(Cancelada),
            // C2
            When(PrazoExpirado)
                .IfElse(c => PrazoDoEstadoAtual(c, nameof(AguardandoAprovacao)),
                    vencido => vencido
                        .ThenAsync(c => CompensarAntesDaExecucao(c, "Prazo de aprovação do orçamento expirado"))
                        .TransitionTo(Cancelada),
                    antigo => antigo.Then(PrazoDeOutroEstado)),
            When(CancelamentoSolicitado)
                .ThenAsync(c => CompensarAntesDaExecucao(c, MotivoDoCancelamento(c.Message)))
                .TransitionTo(Cancelada));

        // ───────────── Pagamento ─────────────
        During(AguardandoPagamento,
            When(PagamentoConfirmado)
                .Then(c =>
                {
                    c.Saga.PagamentoId = c.Message.PagamentoId;
                    c.Saga.PrazoExpiraEm = DateTime.UtcNow + _prazos.Execucao;
                })
                .ThenAsync(async c =>
                {
                    var os = await Ops(c).ObterDadosAsync(c.Saga.OsId, c.CancellationToken);
                    await Enviar(c, FilasComando.IniciarExecucao,
                        Mensagem<IniciarExecucao>(c.Saga) with { Servicos = os.Servicos });
                })
                .Then(Atualizado)
                .TransitionTo(AguardandoExecucao),
            // C2
            When(PagamentoRecusado)
                .ThenAsync(c => CompensarAntesDaExecucao(c, "Pagamento recusado" + Detalhe(c.Message.Motivo)))
                .TransitionTo(Cancelada),
            // C2
            When(PrazoExpirado)
                .IfElse(c => PrazoDoEstadoAtual(c, nameof(AguardandoPagamento)),
                    vencido => vencido
                        .ThenAsync(c => CompensarAntesDaExecucao(c, "Prazo de pagamento expirado"))
                        .TransitionTo(Cancelada),
                    antigo => antigo.Then(PrazoDeOutroEstado)),
            When(CancelamentoSolicitado)
                .ThenAsync(c => CompensarAntesDaExecucao(c, MotivoDoCancelamento(c.Message)))
                .TransitionTo(Cancelada));

        // ───────────── Execução ─────────────
        During(AguardandoExecucao,
            When(ExecucaoIniciada)
                .Then(c => c.Saga.PrazoExpiraEm = null)
                .ThenAsync(c => Status(c, StatusOrdemServico.EmExecucao, "Execução iniciada"))
                .Then(Atualizado)
                .TransitionTo(EmExecucao),
            // Fora de ordem: a finalização chegou antes do aviso de início.
            When(ExecucaoFinalizada)
                .Then(c => c.Saga.PrazoExpiraEm = null)
                .ThenAsync(c => Status(c, StatusOrdemServico.Finalizada, "Execução finalizada"))
                .Then(Atualizado)
                .TransitionTo(Concluida),
            // C3
            When(ExecucaoRejeitada)
                .ThenAsync(c => CompensarComEstorno(c, "Execução rejeitada" + Detalhe(c.Message.Motivo), cancelarExecucao: false))
                .TransitionTo(AguardandoEstorno),
            // C3 + CancelarExecucao
            When(PrazoExpirado)
                .IfElse(c => PrazoDoEstadoAtual(c, nameof(AguardandoExecucao)),
                    vencido => vencido
                        .ThenAsync(c => CompensarComEstorno(c, "Prazo para iniciar a execução expirado", cancelarExecucao: true))
                        .TransitionTo(AguardandoEstorno),
                    antigo => antigo.Then(PrazoDeOutroEstado)),
            When(CancelamentoSolicitado)
                .ThenAsync(c => CompensarComEstorno(c, MotivoDoCancelamento(c.Message), cancelarExecucao: true))
                .TransitionTo(AguardandoEstorno));

        During(EmExecucao,
            When(ExecucaoFinalizada)
                .ThenAsync(c => Status(c, StatusOrdemServico.Finalizada, "Execução finalizada"))
                .Then(Atualizado)
                .TransitionTo(Concluida));

        During(AguardandoEstorno,
            When(PagamentoEstornado)
                .Then(Atualizado)
                .TransitionTo(Cancelada));

        // ───────────── Eventos atrasados ou repetidos: Ignore explícito ─────────────
        // Concluida e Cancelada ignoram tudo. Os casos que exigem ação do billing (pagamento
        // depois do cancelamento etc.) estão na seção 5 do documento de contratos.
        IgnorarForaDe(OrdemServicoAberta);
        IgnorarForaDe(DiagnosticoIniciado, AguardandoDiagnostico);
        IgnorarForaDe(DiagnosticoConcluido, AguardandoDiagnostico, EmDiagnostico);
        IgnorarForaDe(OrcamentoGerado, AguardandoOrcamento);
        IgnorarForaDe(OrcamentoAprovado, AguardandoAprovacao);
        IgnorarForaDe(OrcamentoRecusado, AguardandoAprovacao);
        IgnorarForaDe(PagamentoConfirmado, AguardandoPagamento);
        IgnorarForaDe(PagamentoRecusado, AguardandoPagamento);
        IgnorarForaDe(ExecucaoIniciada, AguardandoExecucao);
        IgnorarForaDe(ExecucaoRejeitada, AguardandoExecucao);
        IgnorarForaDe(ExecucaoFinalizada, AguardandoExecucao, EmExecucao);
        IgnorarForaDe(PagamentoEstornado, AguardandoEstorno);
        IgnorarForaDe(PrazoExpirado, AguardandoAprovacao, AguardandoPagamento, AguardandoExecucao);
        // Em execução, o cancelamento não é aceito (a API responde 409).
        IgnorarForaDe(CancelamentoSolicitado,
            AguardandoDiagnostico, EmDiagnostico, AguardandoOrcamento, AguardandoAprovacao, AguardandoPagamento, AguardandoExecucao);

        // Rede de segurança: combinação de estado e evento esquecida acima não vai para a
        // fila _error; fica registrada no log.
        OnUnhandledEvent(c =>
        {
            LogContext.Warning?.Log("Saga {CorrelationId}: evento {Evento} sem tratamento no estado {Estado}; ignorado.",
                c.Saga.CorrelationId, c.Event.Name, c.CurrentState.Name);
            return c.Ignore();
        });
    }

    /// <summary>Todos os estados da saga, exceto Initial e Final do MassTransit.</summary>
    private IEnumerable<State> EstadosDaSaga =>
    [
        AguardandoDiagnostico, EmDiagnostico, AguardandoOrcamento, AguardandoAprovacao, AguardandoPagamento,
        AguardandoExecucao, EmExecucao, Concluida, AguardandoEstorno, Cancelada
    ];

    private void IgnorarForaDe<T>(Event<T> evento, params State[] tratadoEm) where T : class
    {
        foreach (var estado in EstadosDaSaga.Except(tratadoEm))
            During(estado, Ignore(evento));
    }

    private void Correlacionar<T>(Expression<Func<Event<T>>> evento) where T : class, IMensagemOS =>
        Event(evento, e =>
        {
            e.CorrelateById(c => c.Message.CorrelationId);
            // Evento de uma saga que não existe (id errado ou OS de outro ambiente): descarta e registra.
            e.OnMissingInstance(m => m.ExecuteAsync(c =>
            {
                LogContext.Warning?.Log("Saga {CorrelationId} não encontrada para {Mensagem}; mensagem descartada.",
                    c.Message.CorrelationId, typeof(T).Name);
                return Task.CompletedTask;
            }));
        });

    // ───────────── Ações ─────────────

    private static IOperacoesOsSaga Ops(BehaviorContext<OrdemServicoSagaState> c) =>
        c.GetServiceOrCreateInstance<IOperacoesOsSaga>();

    private static Task Status(BehaviorContext<OrdemServicoSagaState> c, StatusOrdemServico alvo, string motivo) =>
        Ops(c).AplicarStatusAsync(c.Saga.OsId, alvo, motivo, c.CancellationToken);

    private static void Atualizado(BehaviorContext<OrdemServicoSagaState> c) => c.Saga.AtualizadoEm = DateTime.UtcNow;

    private static async Task SolicitarOrcamento(BehaviorContext<OrdemServicoSagaState> c)
    {
        var os = await Ops(c).ObterDadosAsync(c.Saga.OsId, c.CancellationToken);
        await Enviar(c, FilasComando.GerarOrcamento,
            Mensagem<GerarOrcamento>(c.Saga) with { Cliente = os.Cliente, Itens = os.Itens, ValorTotal = os.ValorTotal });
    }

    /// <summary>Cancelamento antes de o orçamento ser pedido: devolve as peças e avisa a execução.</summary>
    private static async Task CompensarAntesDoOrcamento(BehaviorContext<OrdemServicoSagaState> c, string motivo)
    {
        await Ops(c).DevolverPecasAsync(c.Saga.OsId, c.CancellationToken);
        await Enviar(c, FilasComando.CancelarExecucao, Mensagem<CancelarExecucao>(c.Saga) with { Motivo = motivo });
        await CancelarOs(c, motivo);
    }

    /// <summary>C1: o cliente recusou o orçamento (o billing já sabe, não precisa de CancelarOrcamento).</summary>
    private static async Task CompensarRecusa(BehaviorContext<OrdemServicoSagaState> c, string motivo)
    {
        await Ops(c).DevolverPecasAsync(c.Saga.OsId, c.CancellationToken);
        await Enviar(c, FilasComando.CancelarExecucao, Mensagem<CancelarExecucao>(c.Saga) with { Motivo = motivo });
        await CancelarOs(c, motivo);
    }

    /// <summary>C2: prazo de aprovação, pagamento recusado ou prazo de pagamento.</summary>
    private static async Task CompensarAntesDaExecucao(BehaviorContext<OrdemServicoSagaState> c, string motivo)
    {
        await Enviar(c, FilasComando.CancelarOrcamento, Mensagem<CancelarOrcamento>(c.Saga) with { Motivo = motivo });
        await Ops(c).DevolverPecasAsync(c.Saga.OsId, c.CancellationToken);
        await Enviar(c, FilasComando.CancelarExecucao, Mensagem<CancelarExecucao>(c.Saga) with { Motivo = motivo });
        await CancelarOs(c, motivo);
    }

    /// <summary>
    /// C3: execução rejeitada ou prazo de execução. O estorno também cancela o orçamento no
    /// billing. A saga fica em AguardandoEstorno até receber PagamentoEstornado.
    /// </summary>
    private static async Task CompensarComEstorno(BehaviorContext<OrdemServicoSagaState> c, string motivo, bool cancelarExecucao)
    {
        var pagamentoId = c.Saga.PagamentoId
            ?? throw new InvalidOperationException($"Saga {c.Saga.CorrelationId} sem PagamentoId para estornar.");

        await Enviar(c, FilasComando.EstornarPagamento,
            Mensagem<EstornarPagamento>(c.Saga) with { PagamentoId = pagamentoId, Motivo = motivo });
        await Ops(c).DevolverPecasAsync(c.Saga.OsId, c.CancellationToken);
        if (cancelarExecucao)
            await Enviar(c, FilasComando.CancelarExecucao, Mensagem<CancelarExecucao>(c.Saga) with { Motivo = motivo });
        await CancelarOs(c, motivo);
    }

    private static async Task CancelarOs(BehaviorContext<OrdemServicoSagaState> c, string motivo)
    {
        // Toda compensação passa por aqui. A tag é o evento que a disparou, não o texto do
        // motivo, para a métrica ter poucos valores (o texto fica no log e na saga).
        MetricasSaga.Compensada.Add(1, new KeyValuePair<string, object?>("motivo", MotivoDaMetrica(c)));
        c.Saga.MotivoCancelamento = motivo;
        c.Saga.PrazoExpiraEm = null;
        Atualizado(c);
        await Status(c, StatusOrdemServico.Cancelada, motivo);
    }

    /// <summary>Evento que disparou a compensação; o prazo leva junto o estado (ex.: PrazoExpirado.AguardandoPagamento).</summary>
    public static string MotivoDaMetrica(BehaviorContext<OrdemServicoSagaState> c) =>
        c.Event.Name == nameof(PrazoExpirado) ? $"{nameof(PrazoExpirado)}.{c.Saga.CurrentState}" : c.Event.Name;

    private void RegistrarDuracaoDoEstado(BehaviorContext<OrdemServicoSagaState> c)
    {
        if (c.Saga.EstadoDesde is not { } desde || c.Saga.CurrentState == Initial.Name)
            return;
        var segundos = Math.Max(0, (DateTime.UtcNow - desde).TotalSeconds);
        MetricasSaga.DuracaoEtapa.Record(segundos, new KeyValuePair<string, object?>("etapa", c.Saga.CurrentState));
    }

    /// <summary>
    /// O PrazoExpirado só vale para o estado em que foi gerado. Um aviso atrasado, de um
    /// estado que a saga já deixou, não pode cancelar a OS.
    /// </summary>
    private static bool PrazoDoEstadoAtual(BehaviorContext<OrdemServicoSagaState, PrazoExpirado> c, string estado) =>
        c.Message.EstadoNoMomento == estado;

    private static void PrazoDeOutroEstado(BehaviorContext<OrdemServicoSagaState, PrazoExpirado> c) =>
        LogContext.Info?.Log("Saga {CorrelationId}: prazo de {EstadoNoMomento} chegou em {Estado}; ignorado.",
            c.Saga.CorrelationId, c.Message.EstadoNoMomento, c.Saga.CurrentState);

    // ───────────── Mensagens ─────────────

    private static T Mensagem<T>(OrdemServicoSagaState saga) where T : MensagemOS, new() => new()
    {
        CorrelationId = saga.CorrelationId,
        OsId = saga.OsId,
        NumeroOS = saga.NumeroOS,
        OcorridoEm = DateTime.UtcNow
    };

    private static async Task Enviar<T>(BehaviorContext<OrdemServicoSagaState> c, Uri fila, T mensagem) where T : class
    {
        var destino = await c.GetSendEndpoint(fila);
        await destino.Send(mensagem, c.CancellationToken);
    }

    private static string MotivoDoCancelamento(CancelamentoSolicitado m) =>
        $"Cancelamento solicitado por {m.SolicitadoPor}" + Detalhe(m.Motivo);

    private static string Detalhe(string? motivo) => string.IsNullOrWhiteSpace(motivo) ? string.Empty : $": {motivo}";
}
