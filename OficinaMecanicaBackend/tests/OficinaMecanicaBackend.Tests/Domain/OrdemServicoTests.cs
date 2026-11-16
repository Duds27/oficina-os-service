using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Domain.Exceptions;

namespace OficinaMecanicaBackend.Tests.Domain;

public class OrdemServicoTests
{
    private static readonly DateTime Agora = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private static OrdemServico Os(StatusOrdemServico status = StatusOrdemServico.Recebida) => new()
    {
        Id = 42,
        NumeroOS = "OS-2026-000042",
        CorrelationId = Guid.NewGuid(),
        Status = status
    };

    // ────── ValidarTransicao ──────

    [Theory]
    [InlineData(StatusOrdemServico.Recebida, StatusOrdemServico.EmDiagnostico)]
    [InlineData(StatusOrdemServico.EmDiagnostico, StatusOrdemServico.AguardandoAprovacao)]
    [InlineData(StatusOrdemServico.AguardandoAprovacao, StatusOrdemServico.AguardandoPagamento)]
    [InlineData(StatusOrdemServico.AguardandoPagamento, StatusOrdemServico.EmExecucao)]
    [InlineData(StatusOrdemServico.EmExecucao, StatusOrdemServico.Finalizada)]
    [InlineData(StatusOrdemServico.Finalizada, StatusOrdemServico.Entregue)]
    public void ValidarTransicao_PassosDoFluxoNormal_SaoValidos(StatusOrdemServico de, StatusOrdemServico para)
    {
        var (valid, error) = Os(de).ValidarTransicao(para);

        Assert.True(valid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(StatusOrdemServico.Recebida, StatusOrdemServico.AguardandoAprovacao)]
    [InlineData(StatusOrdemServico.AguardandoAprovacao, StatusOrdemServico.EmExecucao)]
    [InlineData(StatusOrdemServico.EmExecucao, StatusOrdemServico.EmDiagnostico)]
    [InlineData(StatusOrdemServico.Entregue, StatusOrdemServico.Finalizada)]
    [InlineData(StatusOrdemServico.Cancelada, StatusOrdemServico.Recebida)]
    [InlineData(StatusOrdemServico.Recebida, StatusOrdemServico.Recebida)]
    public void ValidarTransicao_PuloOuRetrocesso_EInvalido(StatusOrdemServico de, StatusOrdemServico para)
    {
        var (valid, error) = Os(de).ValidarTransicao(para);

        Assert.False(valid);
        Assert.Contains("Transição inválida", error);
    }

    [Theory]
    [InlineData(StatusOrdemServico.Recebida)]
    [InlineData(StatusOrdemServico.EmDiagnostico)]
    [InlineData(StatusOrdemServico.AguardandoAprovacao)]
    [InlineData(StatusOrdemServico.AguardandoPagamento)]
    [InlineData(StatusOrdemServico.EmExecucao)]
    public void ValidarTransicao_StatusNaoEncerrado_PodeSerCancelado(StatusOrdemServico de)
    {
        var (valid, _) = Os(de).ValidarTransicao(StatusOrdemServico.Cancelada);

        Assert.True(valid);
    }

    [Theory]
    [InlineData(StatusOrdemServico.Finalizada)]
    [InlineData(StatusOrdemServico.Entregue)]
    [InlineData(StatusOrdemServico.Cancelada)]
    public void ValidarTransicao_OsEncerrada_NaoPodeSerCancelada(StatusOrdemServico de)
    {
        var (valid, error) = Os(de).ValidarTransicao(StatusOrdemServico.Cancelada);

        Assert.False(valid);
        Assert.Contains("encerrada", error);
    }

    // ────── MudarStatus ──────

    [Fact]
    public void MudarStatus_GravaHistoricoComOrigemMotivoECorrelationId()
    {
        var os = Os();

        var registro = os.MudarStatus(StatusOrdemServico.EmDiagnostico, OrigemMudancaStatus.Saga, "Diagnóstico iniciado", Agora);

        Assert.Equal(StatusOrdemServico.EmDiagnostico, os.Status);
        Assert.Same(registro, Assert.Single(os.Historico));
        Assert.Equal(42, registro.OrdemServicoId);
        Assert.Equal(StatusOrdemServico.Recebida, registro.StatusAnterior);
        Assert.Equal(StatusOrdemServico.EmDiagnostico, registro.StatusNovo);
        Assert.Equal(OrigemMudancaStatus.Saga, registro.Origem);
        Assert.Equal("Diagnóstico iniciado", registro.Motivo);
        Assert.Equal(os.CorrelationId, registro.CorrelationId);
        Assert.Equal(Agora, registro.OcorridoEm);
        Assert.Equal(Agora, os.DataAtualizacao);
    }

    [Fact]
    public void MudarStatus_TransicaoInvalida_LancaExcecaoSemAlterarAOs()
    {
        var os = Os();

        var ex = Assert.Throws<TransicaoStatusInvalidaException>(() =>
            os.MudarStatus(StatusOrdemServico.EmExecucao, OrigemMudancaStatus.Usuario, null, Agora));

        Assert.Equal(StatusOrdemServico.Recebida, ex.StatusAtual);
        Assert.Equal(StatusOrdemServico.EmExecucao, ex.StatusNovo);
        Assert.Equal(StatusOrdemServico.Recebida, os.Status);
        Assert.Empty(os.Historico);
    }

    [Fact]
    public void MudarStatus_AguardandoPagamento_MarcaOrcamentoAprovado()
    {
        var os = Os(StatusOrdemServico.AguardandoAprovacao);

        os.MudarStatus(StatusOrdemServico.AguardandoPagamento, OrigemMudancaStatus.Saga, null, Agora);

        Assert.True(os.OrcamentoAprovado);
        Assert.Equal(Agora, os.DataAprovacaoOrcamento);
    }

    [Theory]
    [InlineData(StatusOrdemServico.AguardandoPagamento, StatusOrdemServico.EmExecucao)]
    [InlineData(StatusOrdemServico.EmExecucao, StatusOrdemServico.Finalizada)]
    [InlineData(StatusOrdemServico.Finalizada, StatusOrdemServico.Entregue)]
    public void MudarStatus_RegistraODataDoMarco(StatusOrdemServico de, StatusOrdemServico para)
    {
        var os = Os(de);

        os.MudarStatus(para, OrigemMudancaStatus.Usuario, null, Agora);

        var marco = para switch
        {
            StatusOrdemServico.EmExecucao => os.DataInicioExecucao,
            StatusOrdemServico.Finalizada => os.DataFinalizacao,
            _ => os.DataEntrega
        };
        Assert.Equal(Agora, marco);
    }

    // ────── AvancarAte ──────

    [Fact]
    public void AvancarAte_EventoForaDeOrdem_AplicaCadaPassoIntermediario()
    {
        // Execução finalizada chegou antes do aviso de início.
        var os = Os(StatusOrdemServico.AguardandoPagamento);

        var registros = os.AvancarAte(StatusOrdemServico.Finalizada, OrigemMudancaStatus.Saga, "Execução finalizada", Agora);

        Assert.Equal(StatusOrdemServico.Finalizada, os.Status);
        Assert.Collection(registros,
            r => Assert.Equal((StatusOrdemServico.AguardandoPagamento, StatusOrdemServico.EmExecucao), (r.StatusAnterior, r.StatusNovo)),
            r => Assert.Equal((StatusOrdemServico.EmExecucao, StatusOrdemServico.Finalizada), (r.StatusAnterior, r.StatusNovo)));
        Assert.Equal(2, os.Historico.Count);
        Assert.NotNull(os.DataInicioExecucao);
        Assert.NotNull(os.DataFinalizacao);
    }

    [Fact]
    public void AvancarAte_JaNoAlvo_NaoFazNada()
    {
        var os = Os(StatusOrdemServico.EmDiagnostico);

        var registros = os.AvancarAte(StatusOrdemServico.EmDiagnostico, OrigemMudancaStatus.Saga, null, Agora);

        Assert.Empty(registros);
        Assert.Empty(os.Historico);
        Assert.Null(os.DataAtualizacao);
    }

    [Fact]
    public void AvancarAte_Cancelada_VaiDireto()
    {
        var os = Os(StatusOrdemServico.AguardandoAprovacao);

        var registros = os.AvancarAte(StatusOrdemServico.Cancelada, OrigemMudancaStatus.Saga, "Orçamento recusado", Agora);

        var registro = Assert.Single(registros);
        Assert.Equal(StatusOrdemServico.AguardandoAprovacao, registro.StatusAnterior);
        Assert.Equal(StatusOrdemServico.Cancelada, os.Status);
    }

    [Theory]
    [InlineData(StatusOrdemServico.EmExecucao, StatusOrdemServico.EmDiagnostico)]
    [InlineData(StatusOrdemServico.Cancelada, StatusOrdemServico.Finalizada)]
    public void AvancarAte_AlvoParaTrasOuOsCancelada_LancaExcecao(StatusOrdemServico de, StatusOrdemServico alvo)
    {
        var os = Os(de);

        Assert.Throws<TransicaoStatusInvalidaException>(() =>
            os.AvancarAte(alvo, OrigemMudancaStatus.Saga, null, Agora));
        Assert.Equal(de, os.Status);
    }

    // ────── Itens e listagem ──────

    [Theory]
    [InlineData(StatusOrdemServico.Recebida, true)]
    [InlineData(StatusOrdemServico.EmDiagnostico, true)]
    [InlineData(StatusOrdemServico.AguardandoAprovacao, false)]
    [InlineData(StatusOrdemServico.AguardandoPagamento, false)]
    [InlineData(StatusOrdemServico.EmExecucao, false)]
    [InlineData(StatusOrdemServico.Finalizada, false)]
    [InlineData(StatusOrdemServico.Cancelada, false)]
    public void PermiteAlterarItens_SoDuranteODiagnostico(StatusOrdemServico status, bool esperado)
    {
        Assert.Equal(esperado, Os(status).PermiteAlterarItens);
    }

    [Theory]
    [InlineData(StatusOrdemServico.Finalizada)]
    [InlineData(StatusOrdemServico.Entregue)]
    [InlineData(StatusOrdemServico.Cancelada)]
    public void VisivelNaListagem_OsEncerradaFicaDeFora(StatusOrdemServico status)
    {
        Assert.False(Os(status).VisivelNaListagem);
    }

    [Fact]
    public void PrioridadeListagem_OrdenaDaMaisAdiantadaParaAMenosAdiantada()
    {
        var ordenados = new[]
            {
                StatusOrdemServico.Recebida, StatusOrdemServico.AguardandoAprovacao, StatusOrdemServico.EmExecucao,
                StatusOrdemServico.EmDiagnostico, StatusOrdemServico.AguardandoPagamento
            }
            .OrderBy(OrdemServico.PrioridadeListagem)
            .ToArray();

        Assert.Equal(new[]
        {
            StatusOrdemServico.EmExecucao, StatusOrdemServico.AguardandoPagamento, StatusOrdemServico.AguardandoAprovacao,
            StatusOrdemServico.EmDiagnostico, StatusOrdemServico.Recebida
        }, ordenados);
    }
}
