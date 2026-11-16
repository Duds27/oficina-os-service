using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Domain.Exceptions;

namespace OficinaMecanica.Domain.Entities;

public class OrdemServico
{
    /// <summary>Caminho normal da OS, do recebimento à entrega. <c>Cancelada</c> fica fora dele.</summary>
    private static readonly StatusOrdemServico[] FluxoNormal =
    [
        StatusOrdemServico.Recebida,
        StatusOrdemServico.EmDiagnostico,
        StatusOrdemServico.AguardandoAprovacao,
        StatusOrdemServico.AguardandoPagamento,
        StatusOrdemServico.EmExecucao,
        StatusOrdemServico.Finalizada,
        StatusOrdemServico.Entregue
    ];

    public int Id { get; set; }
    public string NumeroOS { get; set; } = string.Empty;

    /// <summary>Id da saga da OS, gerado na abertura. Vai em todas as mensagens trocadas com os outros serviços.</summary>
    public Guid CorrelationId { get; set; }

    public int ClienteId { get; set; }
    public int VeiculoId { get; set; }
    public StatusOrdemServico Status { get; set; } = StatusOrdemServico.Recebida;
    public string? Descricao { get; set; }
    public decimal ValorTotal { get; set; }
    public bool OrcamentoAprovado { get; set; }
    public DateTime? DataAprovacaoOrcamento { get; set; }
    public DateTime? DataPrevisaoTermino { get; set; }
    public DateTime? DataInicioExecucao { get; set; }
    public DateTime? DataFinalizacao { get; set; }
    public DateTime? DataEntrega { get; set; }

    /// <summary>Quando as peças da OS voltaram ao estoque por compensação. Evita devolver duas vezes.</summary>
    public DateTime? PecasDevolvidasEm { get; set; }

    public DateTime DataCriacao { get; set; }
    public DateTime? DataAtualizacao { get; set; }

    public Cliente Cliente { get; set; } = null!;
    public Veiculo Veiculo { get; set; } = null!;
    public ICollection<ItemOrdemServico> Itens { get; set; } = new List<ItemOrdemServico>();
    public ICollection<HistoricoStatusOS> Historico { get; set; } = new List<HistoricoStatusOS>();

    // ───────────────────────── Regras de domínio ─────────────────────────

    /// <summary>OS que já saiu do fluxo: concluída (finalizada ou entregue) ou cancelada.</summary>
    public bool Encerrada =>
        Status is StatusOrdemServico.Finalizada or StatusOrdemServico.Entregue or StatusOrdemServico.Cancelada;

    /// <summary>Itens só entram ou saem durante o diagnóstico. Depois disso o orçamento já foi gerado com eles.</summary>
    public bool PermiteAlterarItens =>
        Status is StatusOrdemServico.Recebida or StatusOrdemServico.EmDiagnostico;

    /// <summary>
    /// Valida a transição de status conforme o fluxo da OS. A aprovação do orçamento
    /// e o pagamento são decididos pela saga, então aqui só se confere a sequência.
    /// </summary>
    public (bool valid, string? error) ValidarTransicao(StatusOrdemServico novoStatus)
    {
        if (novoStatus == StatusOrdemServico.Cancelada)
            return Encerrada
                ? (false, $"A OS já está encerrada ({Status}) e não pode ser cancelada.")
                : (true, null);

        var atual = Array.IndexOf(FluxoNormal, Status);
        var novo = Array.IndexOf(FluxoNormal, novoStatus);

        return atual >= 0 && novo == atual + 1
            ? (true, null)
            : (false, $"Transição inválida: {Status} → {novoStatus}.");
    }

    /// <summary>
    /// Muda o status, registra os marcos de data e acrescenta a transição ao histórico.
    /// É o único caminho para alterar o status de uma OS.
    /// </summary>
    /// <exception cref="TransicaoStatusInvalidaException">A transição não é permitida a partir do status atual.</exception>
    public HistoricoStatusOS MudarStatus(
        StatusOrdemServico novoStatus, OrigemMudancaStatus origem, string? motivo, DateTime agora)
    {
        var (valid, error) = ValidarTransicao(novoStatus);
        if (!valid)
            throw new TransicaoStatusInvalidaException(Status, novoStatus, error!);

        var registro = new HistoricoStatusOS
        {
            OrdemServicoId = Id,
            StatusAnterior = Status,
            StatusNovo = novoStatus,
            Origem = origem,
            Motivo = motivo,
            CorrelationId = CorrelationId,
            OcorridoEm = agora
        };

        AplicarStatus(novoStatus, agora);
        Historico.Add(registro);
        return registro;
    }

    /// <summary>
    /// Leva a OS até <paramref name="alvo"/>, passando por cada status intermediário do fluxo normal.
    /// Usado pela saga quando um evento chega antes do anterior (ex.: execução finalizada sem o
    /// aviso de início): o histórico fica com um registro por passo. Não faz nada se a OS já está no alvo.
    /// </summary>
    /// <exception cref="TransicaoStatusInvalidaException">O alvo fica para trás no fluxo ou a OS está encerrada.</exception>
    public IReadOnlyList<HistoricoStatusOS> AvancarAte(
        StatusOrdemServico alvo, OrigemMudancaStatus origem, string? motivo, DateTime agora)
    {
        if (Status == alvo)
            return [];

        if (alvo == StatusOrdemServico.Cancelada)
            return [MudarStatus(alvo, origem, motivo, agora)];

        var atual = Array.IndexOf(FluxoNormal, Status);
        var destino = Array.IndexOf(FluxoNormal, alvo);
        if (atual < 0 || destino < atual)
            throw new TransicaoStatusInvalidaException(Status, alvo,
                $"Não é possível avançar a OS de {Status} até {alvo}.");

        var registros = new List<HistoricoStatusOS>();
        for (var i = atual + 1; i <= destino; i++)
            registros.Add(MudarStatus(FluxoNormal[i], origem, motivo, agora));
        return registros;
    }

    private void AplicarStatus(StatusOrdemServico novoStatus, DateTime agora)
    {
        Status = novoStatus;

        switch (novoStatus)
        {
            case StatusOrdemServico.AguardandoPagamento:
                // Só se chega aqui com o orçamento aprovado pelo cliente (evento do billing).
                OrcamentoAprovado = true;
                DataAprovacaoOrcamento = agora;
                break;
            case StatusOrdemServico.EmExecucao:
                DataInicioExecucao = agora;
                break;
            case StatusOrdemServico.Finalizada:
                DataFinalizacao = agora;
                break;
            case StatusOrdemServico.Entregue:
                DataEntrega = agora;
                break;
        }

        DataAtualizacao = agora;
    }

    /// <summary>OS finalizadas, entregues ou canceladas são omitidas da listagem operacional.</summary>
    public bool VisivelNaListagem => !Encerrada;

    /// <summary>
    /// Prioridade de exibição na listagem, da OS mais adiantada para a menos adiantada:
    /// Em Execução &gt; Aguardando Pagamento &gt; Aguardando Aprovação &gt; Em Diagnóstico &gt; Recebida
    /// (menor número = maior prioridade).
    /// </summary>
    public static int PrioridadeListagem(StatusOrdemServico status) => status switch
    {
        StatusOrdemServico.EmExecucao => 0,
        StatusOrdemServico.AguardandoPagamento => 1,
        StatusOrdemServico.AguardandoAprovacao => 2,
        StatusOrdemServico.EmDiagnostico => 3,
        StatusOrdemServico.Recebida => 4,
        _ => 5
    };
}
