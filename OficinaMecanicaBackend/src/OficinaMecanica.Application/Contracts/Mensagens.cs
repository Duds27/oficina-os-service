using MassTransit;

// Contratos de mensagens v1 (docs/contratos-mensagens.md, seção 2.1).
// Os três serviços usam o mesmo namespace, URN e exchange: mudar um nome ou tipo
// aqui quebra outro repositório. Mudança incompatível vira v2 (seção 1.3).
namespace Oficina.Contracts;

// ───────────── Envelope (não vira exchange no RabbitMQ) ─────────────
[ExcludeFromTopology]
public interface IMensagemOS
{
    Guid CorrelationId { get; }
    int OsId { get; }
    string NumeroOS { get; }
    DateTime OcorridoEm { get; }
}

[ExcludeFromTopology]
public abstract record MensagemOS : IMensagemOS
{
    public Guid CorrelationId { get; init; }
    public int OsId { get; init; }
    public string NumeroOS { get; init; } = string.Empty;
    public DateTime OcorridoEm { get; init; }
}

// ───────────── Tipos auxiliares ─────────────
public record VeiculoInfo
{
    public string Placa { get; init; } = string.Empty;
    public string Marca { get; init; } = string.Empty;
    public string Modelo { get; init; } = string.Empty;
    public int Ano { get; init; }
}

public record ClienteInfo
{
    public int Id { get; init; }
    public string Nome { get; init; } = string.Empty;
    public string? Email { get; init; }
}

public record ItemOrcamentoInfo
{
    /// <summary>"Servico" ou "Peca".</summary>
    public string Tipo { get; init; } = string.Empty;
    /// <summary>Id do serviço ou da peça no os-service.</summary>
    public int ReferenciaId { get; init; }
    public string Descricao { get; init; } = string.Empty;
    public decimal Quantidade { get; init; }
    public decimal PrecoUnitario { get; init; }
}

public record ServicoExecucaoInfo
{
    public string Descricao { get; init; } = string.Empty;
    public decimal Quantidade { get; init; }
}

// ───────────── os → execution ─────────────
[MessageUrn("oficina:solicitar-diagnostico:v1"), EntityName("oficina.solicitar-diagnostico.v1")]
public record SolicitarDiagnostico : MensagemOS
{
    public VeiculoInfo Veiculo { get; init; } = new();
    public string? Descricao { get; init; }
}

[MessageUrn("oficina:iniciar-execucao:v1"), EntityName("oficina.iniciar-execucao.v1")]
public record IniciarExecucao : MensagemOS
{
    public IReadOnlyList<ServicoExecucaoInfo> Servicos { get; init; } = [];
}

[MessageUrn("oficina:cancelar-execucao:v1"), EntityName("oficina.cancelar-execucao.v1")]
public record CancelarExecucao : MensagemOS
{
    public string Motivo { get; init; } = string.Empty;
}

// ───────────── execution → os ─────────────
[MessageUrn("oficina:diagnostico-iniciado:v1"), EntityName("oficina.diagnostico-iniciado.v1")]
public record DiagnosticoIniciado : MensagemOS
{
    public string Mecanico { get; init; } = string.Empty;
}

[MessageUrn("oficina:diagnostico-concluido:v1"), EntityName("oficina.diagnostico-concluido.v1")]
public record DiagnosticoConcluido : MensagemOS
{
    public string? Observacoes { get; init; }
}

[MessageUrn("oficina:execucao-iniciada:v1"), EntityName("oficina.execucao-iniciada.v1")]
public record ExecucaoIniciada : MensagemOS;

[MessageUrn("oficina:execucao-rejeitada:v1"), EntityName("oficina.execucao-rejeitada.v1")]
public record ExecucaoRejeitada : MensagemOS
{
    public string Motivo { get; init; } = string.Empty;
}

[MessageUrn("oficina:execucao-finalizada:v1"), EntityName("oficina.execucao-finalizada.v1")]
public record ExecucaoFinalizada : MensagemOS
{
    public int DuracaoSegundos { get; init; }
}

// ───────────── os → billing ─────────────
[MessageUrn("oficina:gerar-orcamento:v1"), EntityName("oficina.gerar-orcamento.v1")]
public record GerarOrcamento : MensagemOS
{
    public ClienteInfo Cliente { get; init; } = new();
    public IReadOnlyList<ItemOrcamentoInfo> Itens { get; init; } = [];
    /// <summary>Informativo: o billing recalcula a partir dos itens e confere.</summary>
    public decimal ValorTotal { get; init; }
}

[MessageUrn("oficina:cancelar-orcamento:v1"), EntityName("oficina.cancelar-orcamento.v1")]
public record CancelarOrcamento : MensagemOS
{
    public string Motivo { get; init; } = string.Empty;
}

[MessageUrn("oficina:estornar-pagamento:v1"), EntityName("oficina.estornar-pagamento.v1")]
public record EstornarPagamento : MensagemOS
{
    public Guid PagamentoId { get; init; }
    public string Motivo { get; init; } = string.Empty;
}

// ───────────── billing → os ─────────────
[MessageUrn("oficina:orcamento-gerado:v1"), EntityName("oficina.orcamento-gerado.v1")]
public record OrcamentoGerado : MensagemOS
{
    public Guid OrcamentoId { get; init; }
    public decimal ValorTotal { get; init; }
    public DateTime ValidoAte { get; init; }
}

[MessageUrn("oficina:orcamento-aprovado:v1"), EntityName("oficina.orcamento-aprovado.v1")]
public record OrcamentoAprovado : MensagemOS
{
    public Guid OrcamentoId { get; init; }
    public Guid PagamentoId { get; init; }
    public string LinkPagamento { get; init; } = string.Empty;
}

[MessageUrn("oficina:orcamento-recusado:v1"), EntityName("oficina.orcamento-recusado.v1")]
public record OrcamentoRecusado : MensagemOS
{
    public Guid OrcamentoId { get; init; }
    public string? Motivo { get; init; }
}

[MessageUrn("oficina:pagamento-confirmado:v1"), EntityName("oficina.pagamento-confirmado.v1")]
public record PagamentoConfirmado : MensagemOS
{
    public Guid PagamentoId { get; init; }
    /// <summary>Id do pagamento no Mercado Pago (string para não depender do tamanho do número).</summary>
    public string MpPaymentId { get; init; } = string.Empty;
    public decimal Valor { get; init; }
}

[MessageUrn("oficina:pagamento-recusado:v1"), EntityName("oficina.pagamento-recusado.v1")]
public record PagamentoRecusado : MensagemOS
{
    public Guid PagamentoId { get; init; }
    public string Motivo { get; init; } = string.Empty;
}

[MessageUrn("oficina:pagamento-estornado:v1"), EntityName("oficina.pagamento-estornado.v1")]
public record PagamentoEstornado : MensagemOS
{
    public Guid PagamentoId { get; init; }
    public decimal Valor { get; init; }
}

// ───────────── internos do os-service ─────────────
[MessageUrn("oficina:ordem-servico-aberta:v1"), EntityName("oficina.ordem-servico-aberta.v1")]
public record OrdemServicoAberta : MensagemOS;

[MessageUrn("oficina:prazo-expirado:v1"), EntityName("oficina.prazo-expirado.v1")]
public record PrazoExpirado : MensagemOS
{
    public string EstadoNoMomento { get; init; } = string.Empty;
}

[MessageUrn("oficina:cancelamento-solicitado:v1"), EntityName("oficina.cancelamento-solicitado.v1")]
public record CancelamentoSolicitado : MensagemOS
{
    public string Motivo { get; init; } = string.Empty;
    public string SolicitadoPor { get; init; } = string.Empty;
}
