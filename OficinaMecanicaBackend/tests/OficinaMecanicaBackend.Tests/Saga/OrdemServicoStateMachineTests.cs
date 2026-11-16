using Oficina.Contracts;
using OficinaMecanica.Domain.Enums;

namespace OficinaMecanicaBackend.Tests.Saga;

/// <summary>
/// Transições, compensações (C1, C2, C3), prazos e mensagens fora de ordem ou repetidas
/// da saga (docs/contratos-mensagens.md, seção 4).
/// </summary>
public class OrdemServicoStateMachineTests : IAsyncLifetime
{
    private SagaHarness _s = null!;

    public async Task InitializeAsync() => _s = await SagaHarness.IniciarAsync();
    public async Task DisposeAsync() => await _s.DisposeAsync();

    // ───────────── Caminho feliz ─────────────

    [Fact]
    public async Task Abertura_CriaASagaEEnviaSolicitarDiagnosticoParaAFilaDoExecution()
    {
        var saga = await _s.AbrirAsync();

        Assert.Equal(SagaHarness.OsId, saga.OsId);
        Assert.Equal(SagaHarness.NumeroOS, saga.NumeroOS);

        var comando = Assert.Single(_s.Enviados<SolicitarDiagnostico>());
        Assert.Equal(_s.CorrelationId, comando.CorrelationId);
        Assert.Equal("ABC1D23", comando.Veiculo.Placa);
        Assert.Equal("Barulho no motor", comando.Descricao);
        // No transporte em memória, "queue:execution-solicitar-diagnostico" vira loopback://localhost/execution-solicitar-diagnostico.
        Assert.EndsWith("/execution-solicitar-diagnostico", _s.DestinosDe<SolicitarDiagnostico>().Single()!.AbsolutePath);
        Assert.Empty(_s.Ops.Status);
    }

    [Fact]
    public async Task DiagnosticoIniciado_PoeAOsEmDiagnostico()
    {
        await _s.AteEmDiagnosticoAsync();

        var (alvo, motivo) = Assert.Single(_s.Ops.Status);
        Assert.Equal(StatusOrdemServico.EmDiagnostico, alvo);
        Assert.Contains("Carlos", motivo);
    }

    [Fact]
    public async Task DiagnosticoConcluido_EnviaGerarOrcamentoComItensEClienteDaOs()
    {
        await _s.AteAguardandoOrcamentoAsync();

        var comando = Assert.Single(_s.Enviados<GerarOrcamento>());
        Assert.Equal(7, comando.Cliente.Id);
        Assert.Equal(2, comando.Itens.Count);
        Assert.Equal(165.90m, comando.ValorTotal);
        // O status continua Em Diagnóstico até o orçamento ser gerado.
        Assert.Equal([StatusOrdemServico.EmDiagnostico], _s.Ops.Alvos);
    }

    [Fact]
    public async Task OrcamentoGerado_AguardaAprovacaoComPrazo()
    {
        var antes = DateTime.UtcNow;
        var saga = await _s.AteAguardandoAprovacaoAsync();

        Assert.Equal(SagaHarness.OrcamentoId, saga.OrcamentoId);
        Assert.InRange(saga.PrazoExpiraEm!.Value, antes + SagaHarness.Prazos.Aprovacao, DateTime.UtcNow + SagaHarness.Prazos.Aprovacao);
        Assert.Equal(StatusOrdemServico.AguardandoAprovacao, _s.Ops.Alvos[^1]);
    }

    [Fact]
    public async Task OrcamentoAprovado_GuardaPagamentoETrocaOPrazo()
    {
        var antes = DateTime.UtcNow;
        var saga = await _s.AteAguardandoPagamentoAsync();

        Assert.Equal(SagaHarness.PagamentoId, saga.PagamentoId);
        Assert.Equal("https://mp.example/checkout/1", saga.LinkPagamento);
        Assert.InRange(saga.PrazoExpiraEm!.Value, antes + SagaHarness.Prazos.Pagamento, DateTime.UtcNow + SagaHarness.Prazos.Pagamento);
        Assert.Equal(StatusOrdemServico.AguardandoPagamento, _s.Ops.Alvos[^1]);
    }

    [Fact]
    public async Task PagamentoConfirmado_EnviaIniciarExecucaoComOsServicos()
    {
        var antes = DateTime.UtcNow;
        var saga = await _s.AteAguardandoExecucaoAsync();

        var comando = Assert.Single(_s.Enviados<IniciarExecucao>());
        Assert.Equal("Troca de óleo", Assert.Single(comando.Servicos).Descricao);
        Assert.InRange(saga.PrazoExpiraEm!.Value, antes + SagaHarness.Prazos.Execucao, DateTime.UtcNow + SagaHarness.Prazos.Execucao);
        // A OS continua Aguardando Pagamento até a execução começar (tabela 5.2).
        Assert.Equal(StatusOrdemServico.AguardandoPagamento, _s.Ops.Alvos[^1]);
    }

    [Fact]
    public async Task ExecucaoIniciada_PoeAOsEmExecucaoESemPrazo()
    {
        var saga = await _s.AteEmExecucaoAsync();

        Assert.Null(saga.PrazoExpiraEm);
        Assert.Equal(StatusOrdemServico.EmExecucao, _s.Ops.Alvos[^1]);
    }

    [Fact]
    public async Task ExecucaoFinalizada_ConcluiASagaEFinalizaAOs()
    {
        await _s.AteEmExecucaoAsync();

        await _s.PublicarAsync(_s.Msg<ExecucaoFinalizada>() with { DuracaoSegundos = 3600 });

        await _s.EsperarEstadoAsync(m => m.Concluida);
        Assert.Equal(
            [StatusOrdemServico.EmDiagnostico, StatusOrdemServico.AguardandoAprovacao, StatusOrdemServico.AguardandoPagamento,
             StatusOrdemServico.EmExecucao, StatusOrdemServico.Finalizada],
            _s.Ops.Alvos);
        Assert.Equal(0, _s.Ops.Devolucoes);
    }

    // ───────────── Fora de ordem ─────────────

    [Fact]
    public async Task DiagnosticoConcluidoAntesDoInicio_AvancaEPedeOOrcamento()
    {
        await _s.AbrirAsync();

        await _s.PublicarAsync(_s.Msg<DiagnosticoConcluido>());

        await _s.EsperarEstadoAsync(m => m.AguardandoOrcamento);
        Assert.Equal([StatusOrdemServico.EmDiagnostico], _s.Ops.Alvos);
        Assert.Single(_s.Enviados<GerarOrcamento>());
    }

    [Fact]
    public async Task ExecucaoFinalizadaAntesDoInicio_ConcluiASaga()
    {
        await _s.AteAguardandoExecucaoAsync();

        await _s.PublicarAsync(_s.Msg<ExecucaoFinalizada>());

        var saga = await _s.EsperarEstadoAsync(m => m.Concluida);
        Assert.Null(saga.PrazoExpiraEm);
        // A operação de status leva a OS de Aguardando Pagamento a Finalizada passando por Em Execução.
        Assert.Equal(StatusOrdemServico.Finalizada, _s.Ops.Alvos[^1]);
    }

    [Fact]
    public async Task EventoRepetido_EIgnoradoSemNovaMudancaDeStatus()
    {
        await _s.AteEmDiagnosticoAsync();

        await _s.PublicarAsync(_s.Msg<DiagnosticoIniciado>() with { Mecanico = "Carlos" });
        await _s.PublicarAsync(_s.Msg<OrdemServicoAberta>());

        Assert.Equal("EmDiagnostico", _s.EstadoAtual);
        Assert.Single(_s.Ops.Status);
        Assert.Single(_s.Enviados<SolicitarDiagnostico>());
    }

    [Fact]
    public async Task EventoAtrasado_DeEstadoQueJaPassou_EIgnorado()
    {
        await _s.AteAguardandoPagamentoAsync();
        var mudancas = _s.Ops.Status.Count;

        await _s.PublicarAsync(_s.Msg<DiagnosticoIniciado>());
        await _s.PublicarAsync(_s.Msg<OrcamentoGerado>() with { OrcamentoId = SagaHarness.OrcamentoId });

        Assert.Equal("AguardandoPagamento", _s.EstadoAtual);
        Assert.Equal(mudancas, _s.Ops.Status.Count);
    }

    [Fact]
    public async Task EventoDeSagaInexistente_EDescartadoSemErro()
    {
        await _s.PublicarNoBusAsync(new DiagnosticoIniciado { CorrelationId = Guid.NewGuid(), OsId = 999, NumeroOS = "OS-2026-000999" });

        Assert.Empty(_s.Ops.Status);
        Assert.Empty(_s.Saga.Sagas.Select(_ => true));
    }

    // ───────────── C1: orçamento recusado ─────────────

    [Fact]
    public async Task OrcamentoRecusado_C1_DevolvePecasCancelaExecucaoECancelaAOs()
    {
        await _s.AteAguardandoAprovacaoAsync();

        await _s.PublicarAsync(_s.Msg<OrcamentoRecusado>() with { OrcamentoId = SagaHarness.OrcamentoId, Motivo = "Caro demais" });

        var saga = await _s.EsperarEstadoAsync(m => m.Cancelada);
        Assert.Equal(1, _s.Ops.Devolucoes);
        Assert.Single(_s.Enviados<CancelarExecucao>());
        Assert.Empty(_s.Enviados<CancelarOrcamento>());
        Assert.Empty(_s.Enviados<EstornarPagamento>());
        Assert.Equal(StatusOrdemServico.Cancelada, _s.Ops.Alvos[^1]);
        Assert.Contains("Caro demais", saga.MotivoCancelamento);
        Assert.Null(saga.PrazoExpiraEm);
    }

    // ───────────── C2: antes da execução ─────────────

    [Fact]
    public async Task PrazoDeAprovacaoExpirado_C2_CancelaOrcamentoDevolvePecasECancelaExecucao()
    {
        await _s.AteAguardandoAprovacaoAsync();

        await _s.PublicarAsync(_s.Msg<PrazoExpirado>() with { EstadoNoMomento = "AguardandoAprovacao" });

        await _s.EsperarEstadoAsync(m => m.Cancelada);
        AssertC2();
    }

    [Fact]
    public async Task PagamentoRecusado_C2()
    {
        await _s.AteAguardandoPagamentoAsync();

        await _s.PublicarAsync(_s.Msg<PagamentoRecusado>() with { PagamentoId = SagaHarness.PagamentoId, Motivo = "cc_rejected_insufficient_amount" });

        var saga = await _s.EsperarEstadoAsync(m => m.Cancelada);
        AssertC2();
        Assert.Contains("cc_rejected_insufficient_amount", saga.MotivoCancelamento);
    }

    [Fact]
    public async Task PrazoDePagamentoExpirado_C2()
    {
        await _s.AteAguardandoPagamentoAsync();

        await _s.PublicarAsync(_s.Msg<PrazoExpirado>() with { EstadoNoMomento = "AguardandoPagamento" });

        await _s.EsperarEstadoAsync(m => m.Cancelada);
        AssertC2();
    }

    [Fact]
    public async Task PrazoDeOutroEstado_ChegandoAtrasado_NaoCancela()
    {
        // O prazo de aprovação venceu, mas o cliente aprovou antes do aviso ser processado.
        await _s.AteAguardandoPagamentoAsync();

        await _s.PublicarAsync(_s.Msg<PrazoExpirado>() with { EstadoNoMomento = "AguardandoAprovacao" });

        Assert.Equal("AguardandoPagamento", _s.EstadoAtual);
        Assert.Empty(_s.Enviados<CancelarOrcamento>());
        Assert.Equal(0, _s.Ops.Devolucoes);
    }

    private void AssertC2()
    {
        var cancelar = Assert.Single(_s.Enviados<CancelarOrcamento>());
        Assert.Equal(_s.CorrelationId, cancelar.CorrelationId);
        Assert.Single(_s.Enviados<CancelarExecucao>());
        Assert.Empty(_s.Enviados<EstornarPagamento>());
        Assert.Equal(1, _s.Ops.Devolucoes);
        Assert.Equal(StatusOrdemServico.Cancelada, _s.Ops.Alvos[^1]);
    }

    // ───────────── C3: depois do pagamento ─────────────

    [Fact]
    public async Task ExecucaoRejeitada_C3_EstornaDevolvePecasEAguardaOEstorno()
    {
        await _s.AteAguardandoExecucaoAsync();

        await _s.PublicarAsync(_s.Msg<ExecucaoRejeitada>() with { Motivo = "Sem mecânico disponível" });

        var saga = await _s.EsperarEstadoAsync(m => m.AguardandoEstorno);
        var estorno = Assert.Single(_s.Enviados<EstornarPagamento>());
        Assert.Equal(SagaHarness.PagamentoId, estorno.PagamentoId);
        Assert.Equal(1, _s.Ops.Devolucoes);
        Assert.Empty(_s.Enviados<CancelarExecucao>());
        Assert.Equal(StatusOrdemServico.Cancelada, _s.Ops.Alvos[^1]);
        Assert.Null(saga.PrazoExpiraEm);

        await _s.PublicarAsync(_s.Msg<PagamentoEstornado>() with { PagamentoId = SagaHarness.PagamentoId, Valor = 165.90m });

        await _s.EsperarEstadoAsync(m => m.Cancelada);
        Assert.Equal(1, _s.Ops.Devolucoes);
    }

    [Fact]
    public async Task PrazoDeExecucaoExpirado_C3_TambemCancelaAExecucao()
    {
        await _s.AteAguardandoExecucaoAsync();

        await _s.PublicarAsync(_s.Msg<PrazoExpirado>() with { EstadoNoMomento = "AguardandoExecucao" });

        await _s.EsperarEstadoAsync(m => m.AguardandoEstorno);
        Assert.Single(_s.Enviados<EstornarPagamento>());
        Assert.Single(_s.Enviados<CancelarExecucao>());
        Assert.Equal(1, _s.Ops.Devolucoes);
    }

    // ───────────── Depois do fim ─────────────

    [Fact]
    public async Task SagaCancelada_IgnoraEventosQueChegamDepois()
    {
        await _s.AteAguardandoAprovacaoAsync();
        await _s.PublicarAsync(_s.Msg<OrcamentoRecusado>() with { OrcamentoId = SagaHarness.OrcamentoId });
        await _s.EsperarEstadoAsync(m => m.Cancelada);
        var mudancas = _s.Ops.Status.Count;

        // Corridas da seção 5: o billing trata o pagamento tardio e estorna sozinho.
        await _s.PublicarAsync(_s.Msg<OrcamentoAprovado>() with { PagamentoId = SagaHarness.PagamentoId });
        await _s.PublicarAsync(_s.Msg<PagamentoConfirmado>() with { PagamentoId = SagaHarness.PagamentoId });
        await _s.PublicarAsync(_s.Msg<PagamentoEstornado>() with { PagamentoId = SagaHarness.PagamentoId });
        await _s.PublicarAsync(_s.Msg<PrazoExpirado>() with { EstadoNoMomento = "AguardandoAprovacao" });

        Assert.Equal("Cancelada", _s.EstadoAtual);
        Assert.Equal(mudancas, _s.Ops.Status.Count);
        Assert.Empty(_s.Enviados<IniciarExecucao>());
    }

    [Fact]
    public async Task SagaConcluida_IgnoraEventosQueChegamDepois()
    {
        await _s.AteEmExecucaoAsync();
        await _s.PublicarAsync(_s.Msg<ExecucaoFinalizada>());
        await _s.EsperarEstadoAsync(m => m.Concluida);

        await _s.PublicarAsync(_s.Msg<ExecucaoRejeitada>() with { Motivo = "atrasado" });
        await _s.PublicarAsync(_s.Msg<CancelamentoSolicitado>() with { Motivo = "teste", SolicitadoPor = "atendente" });

        Assert.Equal("Concluida", _s.EstadoAtual);
        Assert.Empty(_s.Enviados<EstornarPagamento>());
    }

    // ───────────── Cancelamento manual ─────────────

    [Fact]
    public async Task CancelamentoDuranteODiagnostico_DevolvePecasECancelaExecucao()
    {
        await _s.AteEmDiagnosticoAsync();

        await _s.PublicarAsync(_s.Msg<CancelamentoSolicitado>() with { Motivo = "Cliente desistiu", SolicitadoPor = "atendente" });

        var saga = await _s.EsperarEstadoAsync(m => m.Cancelada);
        Assert.Single(_s.Enviados<CancelarExecucao>());
        Assert.Empty(_s.Enviados<CancelarOrcamento>());
        Assert.Equal(1, _s.Ops.Devolucoes);
        Assert.Contains("atendente", saga.MotivoCancelamento);
    }

    [Fact]
    public async Task CancelamentoComOrcamentoPedido_TambemCancelaOOrcamento()
    {
        await _s.AteAguardandoOrcamentoAsync();

        await _s.PublicarAsync(_s.Msg<CancelamentoSolicitado>() with { Motivo = "Cliente desistiu", SolicitadoPor = "atendente" });

        await _s.EsperarEstadoAsync(m => m.Cancelada);
        AssertC2();
    }

    [Fact]
    public async Task CancelamentoDepoisDoPagamento_EstornaECancelaExecucao()
    {
        await _s.AteAguardandoExecucaoAsync();

        await _s.PublicarAsync(_s.Msg<CancelamentoSolicitado>() with { Motivo = "Cliente desistiu", SolicitadoPor = "atendente" });

        await _s.EsperarEstadoAsync(m => m.AguardandoEstorno);
        Assert.Single(_s.Enviados<EstornarPagamento>());
        Assert.Single(_s.Enviados<CancelarExecucao>());
    }

    [Fact]
    public async Task CancelamentoEmExecucao_EIgnorado()
    {
        await _s.AteEmExecucaoAsync();

        await _s.PublicarAsync(_s.Msg<CancelamentoSolicitado>() with { Motivo = "tarde demais", SolicitadoPor = "atendente" });

        Assert.Equal("EmExecucao", _s.EstadoAtual);
        Assert.Empty(_s.Enviados<EstornarPagamento>());
    }
}
