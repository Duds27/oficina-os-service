using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MassTransit.Testing;
using Oficina.Contracts;
using OficinaMecanica.Domain.Enums;
using OficinaMecanicaBackend.Tests.Infrastructure;

namespace OficinaMecanicaBackend.Tests.Integration;

[Collection("Integration")]
public class OrdensServicoControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestHarness _harness;

    public OrdensServicoControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _harness = factory.Services.GetTestHarness();
    }

    private async Task AuthorizeAsync()
    {
        var token = await CustomWebApplicationFactory.GetAuthTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    // Cria toda a base necessária e retorna os IDs
    private async Task<(int clienteId, int veiculoId, int servicoId, int pecaId)> SeedBaseAsync()
    {
        // Usa um CPF válido único para cada chamada
        var clienteResp = await _client.PostAsJsonAsync("/api/clientes",
            new { CpfCnpj = "529.982.247-25", Nome = "Cliente Base" });

        if (!clienteResp.IsSuccessStatusCode)
        {
            // Já existe — busca na lista
            var lista = await _client.GetFromJsonAsync<List<ClienteResult>>("/api/clientes");
            var existente = lista!.First(x => x.Nome == "Cliente Base");
            var veiculoR2 = await _client.PostAsJsonAsync("/api/veiculos",
                new { Placa = $"TES{Random.Shared.Next(1000, 9999)}", Marca = "Ford", Modelo = "Ka", Ano = 2020, ClienteId = existente.Id });
            var v2 = await veiculoR2.Content.ReadFromJsonAsync<IdResult>();
            var sResp2 = await _client.PostAsJsonAsync("/api/servicos",
                new { Nome = "Alinhamento", PrecoBase = 80m, Ativo = true });
            var s2 = await sResp2.Content.ReadFromJsonAsync<IdResult>();
            var pResp2 = await _client.PostAsJsonAsync("/api/pecas",
                new { Nome = "Óleo", PrecoUnitario = 30m, QuantidadeEstoque = 20, QuantidadeMinima = 2, Ativo = true });
            var p2 = await pResp2.Content.ReadFromJsonAsync<IdResult>();
            return (existente.Id, v2!.Id, s2!.Id, p2!.Id);
        }

        var cliente = await clienteResp.Content.ReadFromJsonAsync<IdResult>();

        var veiculoResp = await _client.PostAsJsonAsync("/api/veiculos",
            new { Placa = $"TES{Random.Shared.Next(1000, 9999)}", Marca = "Ford", Modelo = "Ka", Ano = 2020, ClienteId = cliente!.Id });
        var veiculo = await veiculoResp.Content.ReadFromJsonAsync<IdResult>();

        var servicoResp = await _client.PostAsJsonAsync("/api/servicos",
            new { Nome = "Alinhamento", PrecoBase = 80m, Ativo = true });
        var servico = await servicoResp.Content.ReadFromJsonAsync<IdResult>();

        var pecaResp = await _client.PostAsJsonAsync("/api/pecas",
            new { Nome = "Óleo", PrecoUnitario = 30m, QuantidadeEstoque = 20, QuantidadeMinima = 2, Ativo = true });
        var peca = await pecaResp.Content.ReadFromJsonAsync<IdResult>();

        return (cliente.Id, veiculo!.Id, servico!.Id, peca!.Id);
    }

    [Fact]
    public async Task GetStatus_SemToken_Returns200()
    {
        await AuthorizeAsync();
        var (cId, vId, _, _) = await SeedBaseAsync();

        var osResp = await _client.PostAsJsonAsync("/api/ordens-servico",
            new { ClienteId = cId, VeiculoId = vId });
        var os = await osResp.Content.ReadFromJsonAsync<IdResult>();

        // Remove o token para simular cliente não autenticado
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.GetAsync($"/api/ordens-servico/{os!.Id}/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Entregar_OsNaoFinalizada_Returns400()
    {
        await AuthorizeAsync();
        var (cId, vId, _, _) = await SeedBaseAsync();

        var osResp = await _client.PostAsJsonAsync("/api/ordens-servico",
            new { ClienteId = cId, VeiculoId = vId });
        var os = await osResp.Content.ReadFromJsonAsync<IdResult>();

        var response = await _client.PostAsync($"/api/ordens-servico/{os!.Id}/entregar", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FluxoCompleto_PelaSaga_AteEntregue()
    {
        var os = await CenarioSaga.CriarOsAsync(_client, _factory.Services, comPeca: true);
        await CenarioSaga.LevarAteAsync(_harness, _factory.Services, os, "Concluida");

        // Só a entrega é feita pelo atendente.
        var entregar = await _client.PostAsync($"/api/ordens-servico/{os.Id}/entregar", null);
        Assert.Equal(HttpStatusCode.OK, entregar.StatusCode);
        Assert.Equal(StatusOrdemServico.Entregue, (await entregar.Content.ReadFromJsonAsync<OrdemServicoStatusResult>())!.Status);

        var historico = (await _client.GetFromJsonAsync<List<HistoricoResult>>($"/api/ordens-servico/{os.Id}/historico"))!;
        Assert.Equal(
            ["EmDiagnostico", "AguardandoAprovacao", "AguardandoPagamento", "EmExecucao", "Finalizada", "Entregue"],
            historico.Select(h => h.StatusNovo));
        Assert.All(historico.SkipLast(1), h => Assert.Equal("Saga", h.Origem));
        Assert.Equal("Usuario", historico[^1].Origem);

        var saga = await _client.GetFromJsonAsync<SagaResult>($"/api/ordens-servico/{os.Id}/saga");
        Assert.Equal("Concluida", saga!.Estado);
        Assert.Equal(os.CorrelationId, saga.CorrelationId);

        // O cliente acompanha sem token.
        _client.DefaultRequestHeaders.Authorization = null;
        var publico = await _client.GetFromJsonAsync<StatusPublicoResult>($"/api/ordens-servico/{os.Id}/status");
        Assert.Equal("Entregue", publico!.StatusDescricao);
    }

    [Fact]
    public async Task Cancelar_Returns202ESagaCancelaAOs()
    {
        var os = await CenarioSaga.CriarOsAsync(_client, _factory.Services, comPeca: true);
        await CenarioSaga.EsperarSagaAsync(_factory.Services, os.CorrelationId, "AguardandoDiagnostico");

        var resp = await _client.PostAsJsonAsync($"/api/ordens-servico/{os.Id}/cancelar", new { Motivo = "Cliente desistiu" });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.EndsWith($"/api/ordens-servico/{os.Id}/saga", resp.Headers.Location!.ToString());
        var saga = await CenarioSaga.EsperarSagaAsync(_factory.Services, os.CorrelationId, "Cancelada");
        Assert.Contains("admin", saga.MotivoCancelamento);
        Assert.Contains("Cliente desistiu", saga.MotivoCancelamento);

        var ordem = await _client.GetFromJsonAsync<OrdemServicoStatusResult>($"/api/ordens-servico/{os.Id}");
        Assert.Equal(StatusOrdemServico.Cancelada, ordem!.Status);
        var lista = await _client.GetFromJsonAsync<List<OrdemServicoStatusResult>>("/api/ordens-servico");
        Assert.DoesNotContain(lista!, o => o.Id == os.Id);
    }

    [Fact]
    public async Task Cancelar_OsEmExecucao_Returns409()
    {
        var os = await CenarioSaga.CriarOsAsync(_client, _factory.Services);
        await CenarioSaga.LevarAteAsync(_harness, _factory.Services, os, "EmExecucao");

        var resp = await _client.PostAsJsonAsync($"/api/ordens-servico/{os.Id}/cancelar", new { Motivo = "tarde" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task GetSaga_OsInexistente_Returns404()
    {
        await AuthorizeAsync();

        var resp = await _client.GetAsync("/api/ordens-servico/999999/saga");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task RotasDaFase3_DeAprovacaoEStatusManual_ForamRemovidas()
    {
        await AuthorizeAsync();
        var (cId, vId, _, _) = await SeedBaseAsync();
        var os = await (await _client.PostAsJsonAsync("/api/ordens-servico",
            new { ClienteId = cId, VeiculoId = vId })).Content.ReadFromJsonAsync<IdResult>();

        // A aprovação passou para o billing-service, e as transições para a saga.
        var aprovar = await _client.PutAsJsonAsync($"/api/ordens-servico/{os!.Id}/aprovar-orcamento", new { Aprovado = true });
        var status = await _client.PutAsJsonAsync($"/api/ordens-servico/{os.Id}/status", new { NovoStatus = 1 });

        Assert.Contains(aprovar.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Contains(status.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    [Fact]
    public async Task AdicionarPeca_DecrementaEstoque()
    {
        await AuthorizeAsync();
        var (cId, vId, _, pecaId) = await SeedBaseAsync();

        // Verifica estoque inicial
        var pecaInicial = await _client.GetFromJsonAsync<PecaResult>($"/api/pecas/{pecaId}");
        var estoqueInicial = pecaInicial!.QuantidadeEstoque;

        var osResp = await _client.PostAsJsonAsync("/api/ordens-servico",
            new { ClienteId = cId, VeiculoId = vId });
        var os = await osResp.Content.ReadFromJsonAsync<IdResult>();

        await _client.PostAsJsonAsync($"/api/ordens-servico/{os!.Id}/itens",
            new { Tipo = (int)TipoItemOrdemServico.Peca, PecaId = pecaId, Quantidade = 2 });

        var pecaApos = await _client.GetFromJsonAsync<PecaResult>($"/api/pecas/{pecaId}");
        Assert.Equal(estoqueInicial - 2, pecaApos!.QuantidadeEstoque);
    }

    [Fact]
    public async Task RemoverPeca_RestaurarEstoque()
    {
        await AuthorizeAsync();
        var (cId, vId, _, pecaId) = await SeedBaseAsync();

        var osResp = await _client.PostAsJsonAsync("/api/ordens-servico",
            new { ClienteId = cId, VeiculoId = vId });
        var os = await osResp.Content.ReadFromJsonAsync<IdResult>();

        // Adiciona peça
        await _client.PostAsJsonAsync($"/api/ordens-servico/{os!.Id}/itens",
            new { Tipo = (int)TipoItemOrdemServico.Peca, PecaId = pecaId, Quantidade = 3 });

        var pecaApos = await _client.GetFromJsonAsync<PecaResult>($"/api/pecas/{pecaId}");
        var estoqueAposAdicao = pecaApos!.QuantidadeEstoque;

        // Busca o item para obter o ID
        var osDetalhes = await _client.GetFromJsonAsync<OrdemServicoResult>($"/api/ordens-servico/{os.Id}");
        var itemId = osDetalhes!.Itens.First().Id;

        // Remove o item
        await _client.DeleteAsync($"/api/ordens-servico/{os.Id}/itens/{itemId}");

        var pecaFinal = await _client.GetFromJsonAsync<PecaResult>($"/api/pecas/{pecaId}");
        Assert.Equal(estoqueAposAdicao + 3, pecaFinal!.QuantidadeEstoque);
    }

    [Fact]
    public async Task Listagem_ExcluiFinalizadasEEntregues()
    {
        var os = await CenarioSaga.CriarOsAsync(_client, _factory.Services);
        await CenarioSaga.LevarAteAsync(_harness, _factory.Services, os, "Concluida");

        // Listagem padrão não deve conter a OS finalizada
        var lista = await _client.GetFromJsonAsync<List<OrdemServicoStatusResult>>("/api/ordens-servico");
        Assert.DoesNotContain(lista!, o => o.Id == os.Id);

        // Com incluirConcluidas=true ela reaparece
        var listaCompleta = await _client.GetFromJsonAsync<List<OrdemServicoStatusResult>>(
            "/api/ordens-servico?incluirConcluidas=true");
        Assert.Contains(listaCompleta!, o => o.Id == os.Id && o.Status == StatusOrdemServico.Finalizada);
    }

    [Fact]
    public async Task TempoMedioExecucao_Returns200ComPayload()
    {
        await AuthorizeAsync();

        var response = await _client.GetAsync("/api/ordens-servico/tempo-medio-execucao");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<TempoMedioExecucaoResult>();
        Assert.NotNull(payload);
        Assert.True(payload!.OrdensConsideradas >= 0);
        Assert.False(string.IsNullOrEmpty(payload.TempoMedioFormatado));
    }

    [Fact]
    public async Task TempoMedioExecucao_ComOSFinalizada_ContabilizaOrdem()
    {
        // A saga grava o início (ExecucaoIniciada) e o fim (ExecucaoFinalizada) da execução.
        var os = await CenarioSaga.CriarOsAsync(_client, _factory.Services);
        await CenarioSaga.LevarAteAsync(_harness, _factory.Services, os, "Concluida");

        var payload = await _client.GetFromJsonAsync<TempoMedioExecucaoResult>(
            "/api/ordens-servico/tempo-medio-execucao");

        Assert.NotNull(payload);
        Assert.True(payload!.OrdensConsideradas >= 1);
    }

    [Fact]
    public async Task CriarOS_PublicaOrdemServicoAbertaPeloOutbox()
    {
        await AuthorizeAsync();
        var (cId, vId, _, _) = await SeedBaseAsync();

        var osResp = await _client.PostAsJsonAsync("/api/ordens-servico",
            new { ClienteId = cId, VeiculoId = vId });
        Assert.Equal(HttpStatusCode.Created, osResp.StatusCode);
        var os = await osResp.Content.ReadFromJsonAsync<OrdemServicoNumeroResult>();

        // O evento sai do OutboxMessage (MySQL) depois do commit e chega ao bus.
        await _harness.EsperarPublicadaAsync<OrdemServicoAberta>(m => m.OsId == os!.Id && m.NumeroOS == os.NumeroOS);
    }

    // ── DTOs para deserialização ──
    private record OrdemServicoNumeroResult(int Id, string NumeroOS);
    private record IdResult(int Id);
    private record OrdemServicoStatusResult(int Id, StatusOrdemServico Status);
    private record TempoMedioExecucaoResult(
        int OrdensConsideradas,
        double TempoMedioSegundos,
        double TempoMedioMinutos,
        double TempoMedioHoras,
        string TempoMedioFormatado);
    private record ClienteResult(int Id, string Nome);
    private record PecaResult(int Id, int QuantidadeEstoque);
    private record ItemResult(int Id);
    private record OrdemServicoResult(int Id, IEnumerable<ItemResult> Itens);
    private record HistoricoResult(string StatusAnterior, string StatusNovo, string Origem, string? Motivo, DateTime OcorridoEm);
    private record SagaResult(Guid CorrelationId, string Estado, DateTime? PrazoExpiraEm);
    private record StatusPublicoResult(string NumeroOS, string StatusDescricao);
}
