namespace Oficina.Contracts;

/// <summary>
/// Filas de comando dos outros serviços (docs/contratos-mensagens.md, seção 3).
/// A saga envia cada comando com endereço explícito, sem EndpointConvention,
/// que é estático e global ao processo.
/// </summary>
public static class FilasComando
{
    public static readonly Uri SolicitarDiagnostico = new("queue:execution-solicitar-diagnostico");
    public static readonly Uri IniciarExecucao = new("queue:execution-iniciar-execucao");
    public static readonly Uri CancelarExecucao = new("queue:execution-cancelar-execucao");
    public static readonly Uri GerarOrcamento = new("queue:billing-gerar-orcamento");
    public static readonly Uri CancelarOrcamento = new("queue:billing-cancelar-orcamento");
    public static readonly Uri EstornarPagamento = new("queue:billing-estornar-pagamento");
}

/// <summary>Filas do próprio os-service.</summary>
public static class FilasOs
{
    /// <summary>Recebe todos os eventos do catálogo que interessam à saga, mais os internos.</summary>
    public const string Saga = "os-ordem-servico-saga";
}
