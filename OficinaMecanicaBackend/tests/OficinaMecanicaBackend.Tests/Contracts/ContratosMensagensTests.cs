using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MassTransit;
using Oficina.Contracts;

namespace OficinaMecanicaBackend.Tests.Contracts;

/// <summary>
/// Testes de contrato (docs/contratos-mensagens.md, seção 6). Quebrar um destes testes
/// significa quebrar o billing-service ou o execution-service sem perceber.
/// </summary>
public partial class ContratosMensagensTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Campos de cada mensagem além do envelope, como no catálogo (seção 2).</summary>
    public static TheoryData<Type, string[]> Catalogo => new()
    {
        { typeof(SolicitarDiagnostico), ["Veiculo", "Descricao"] },
        { typeof(DiagnosticoIniciado), ["Mecanico"] },
        { typeof(DiagnosticoConcluido), ["Observacoes"] },
        { typeof(GerarOrcamento), ["Cliente", "Itens", "ValorTotal"] },
        { typeof(OrcamentoGerado), ["OrcamentoId", "ValorTotal", "ValidoAte"] },
        { typeof(OrcamentoAprovado), ["OrcamentoId", "PagamentoId", "LinkPagamento"] },
        { typeof(OrcamentoRecusado), ["OrcamentoId", "Motivo"] },
        { typeof(PagamentoConfirmado), ["PagamentoId", "MpPaymentId", "Valor"] },
        { typeof(PagamentoRecusado), ["PagamentoId", "Motivo"] },
        { typeof(IniciarExecucao), ["Servicos"] },
        { typeof(ExecucaoIniciada), [] },
        { typeof(ExecucaoRejeitada), ["Motivo"] },
        { typeof(ExecucaoFinalizada), ["DuracaoSegundos"] },
        { typeof(CancelarExecucao), ["Motivo"] },
        { typeof(CancelarOrcamento), ["Motivo"] },
        { typeof(EstornarPagamento), ["PagamentoId", "Motivo"] },
        { typeof(PagamentoEstornado), ["PagamentoId", "Valor"] },
        { typeof(OrdemServicoAberta), [] },
        { typeof(PrazoExpirado), ["EstadoNoMomento"] },
        { typeof(CancelamentoSolicitado), ["Motivo", "SolicitadoPor"] }
    };

    private static readonly string[] Envelope = ["CorrelationId", "OsId", "NumeroOS", "OcorridoEm"];

    [Fact]
    public void Catalogo_TemAs20MensagensDoNamespaceDeContratos()
    {
        var mensagens = typeof(MensagemOS).Assembly.GetTypes()
            .Where(t => t.Namespace == "Oficina.Contracts" && !t.IsAbstract && typeof(MensagemOS).IsAssignableFrom(t))
            .ToHashSet();

        var catalogo = Catalogo.Select(linha => (Type)linha[0]).ToHashSet();

        Assert.Equal(20, mensagens.Count);
        Assert.True(catalogo.SetEquals(mensagens), "Mensagem nova ou removida: atualize o catálogo e docs/contratos-mensagens.md.");
    }

    [Theory]
    [MemberData(nameof(Catalogo))]
    public void Mensagem_TemUrnEExchangeDerivadosDoNome(Type tipo, string[] _)
    {
        var kebab = Kebab(tipo.Name);

        var urn = tipo.GetCustomAttribute<MessageUrnAttribute>();
        var entity = tipo.GetCustomAttribute<EntityNameAttribute>();

        Assert.NotNull(urn);
        Assert.NotNull(entity);
        Assert.Equal($"urn:message:oficina:{kebab}:v1", urn!.Urn.ToString());
        Assert.Equal($"oficina.{kebab}.v1", entity!.EntityName);
    }

    [Theory]
    [MemberData(nameof(Catalogo))]
    public void Mensagem_TemExatamenteOsCamposDoCatalogo(Type tipo, string[] campos)
    {
        var propriedades = tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .Order()
            .ToArray();

        Assert.Equal(Envelope.Concat(campos).Order().ToArray(), propriedades);
    }

    [Fact]
    public void Envelope_NaoViraExchange()
    {
        Assert.NotNull(typeof(MensagemOS).GetCustomAttribute<ExcludeFromTopologyAttribute>());
        Assert.NotNull(typeof(IMensagemOS).GetCustomAttribute<ExcludeFromTopologyAttribute>());
    }

    [Fact]
    public void GerarOrcamento_ExemploNoFioDoDocumento_DesserializaTodosOsCampos()
    {
        // docs/contratos-mensagens.md, seção 2.2
        const string exemplo = """
            {
              "correlationId": "3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11",
              "osId": 42,
              "numeroOS": "OS-20261115-0042",
              "ocorridoEm": "2026-11-15T14:03:27.512Z",
              "cliente": { "id": 7, "nome": "Maria Souza", "email": "maria@example.com" },
              "itens": [
                { "tipo": "Servico", "referenciaId": 3, "descricao": "Troca de óleo", "quantidade": 1, "precoUnitario": 120.00 },
                { "tipo": "Peca", "referenciaId": 15, "descricao": "Filtro de óleo", "quantidade": 1, "precoUnitario": 45.90 }
              ],
              "valorTotal": 165.90
            }
            """;

        var msg = JsonSerializer.Deserialize<GerarOrcamento>(exemplo, Json)!;

        Assert.Equal(Guid.Parse("3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11"), msg.CorrelationId);
        Assert.Equal(42, msg.OsId);
        Assert.Equal("OS-20261115-0042", msg.NumeroOS);
        Assert.Equal(new DateTime(2026, 11, 15, 14, 3, 27, 512, DateTimeKind.Utc), msg.OcorridoEm.ToUniversalTime());
        Assert.Equal(new ClienteInfo { Id = 7, Nome = "Maria Souza", Email = "maria@example.com" }, msg.Cliente);
        Assert.Equal(2, msg.Itens.Count);
        Assert.Equal(new ItemOrcamentoInfo { Tipo = "Peca", ReferenciaId = 15, Descricao = "Filtro de óleo", Quantidade = 1, PrecoUnitario = 45.90m }, msg.Itens[1]);
        Assert.Equal(165.90m, msg.ValorTotal);
    }

    [Fact]
    public void Mensagem_SerializaCamposEmCamelCaseEListaVaziaNuncaNula()
    {
        var json = JsonSerializer.Serialize(new IniciarExecucao { OsId = 1, NumeroOS = "OS-2026-000001" }, Json);

        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, raiz.GetProperty("servicos").ValueKind);
        Assert.Equal(0, raiz.GetProperty("servicos").GetArrayLength());
        Assert.True(raiz.TryGetProperty("numeroOS", out _));
        Assert.True(raiz.TryGetProperty("correlationId", out _));
    }

    private static string Kebab(string nome) => MaiusculaInterna().Replace(nome, "-$1").ToLowerInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex MaiusculaInterna();
}
