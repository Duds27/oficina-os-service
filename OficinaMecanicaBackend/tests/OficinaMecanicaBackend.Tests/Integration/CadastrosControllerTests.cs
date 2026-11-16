using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using OficinaMecanica.Infrastructure.Data;
using OficinaMecanicaBackend.Tests.Infrastructure;

namespace OficinaMecanicaBackend.Tests.Integration;

/// <summary>
/// CRUD dos cadastros (clientes, veículos, peças e serviços) pela API, com MySQL real:
/// controllers, validação, casos de uso e repositórios, incluindo os caminhos de erro.
/// </summary>
[Collection("Integration")]
public class CadastrosControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CadastrosControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private record IdResult(int Id);
    private record ClienteResult(int Id, string CpfCnpj, string Nome, string? Email, bool Ativo);
    private record VeiculoResult(int Id, string Placa, string Marca, string Modelo, int Ano, string? Cor, int ClienteId);
    private record PecaResult(int Id, string Nome, string? Codigo, decimal PrecoUnitario, int QuantidadeEstoque, int QuantidadeMinima, bool EstoqueBaixo);
    private record ServicoResult(int Id, string Nome, decimal PrecoBase, decimal? TempoEstimadoHoras, bool Ativo);

    private async Task AutenticarAsync()
    {
        var token = await CustomWebApplicationFactory.GetAuthTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<int> CriarClienteAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/clientes", new { CpfCnpj = CenarioSaga.GerarCpf(), Nome = "Cliente Cadastro" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<IdResult>())!.Id;
    }

    private static string Placa() => $"CAD{Random.Shared.Next(1000, 9999)}";

    // ───────────── Clientes ─────────────

    [Fact]
    public async Task Clientes_ObterAtualizarEExcluir()
    {
        await AutenticarAsync();
        var id = await CriarClienteAsync();

        var obtido = await _client.GetFromJsonAsync<ClienteResult>($"/api/clientes/{id}");
        Assert.Equal("Cliente Cadastro", obtido!.Nome);

        var atualizado = await _client.PutAsJsonAsync($"/api/clientes/{id}",
            new { Nome = "Cliente Atualizado", Email = "novo@example.com", Ativo = false });
        Assert.Equal(HttpStatusCode.OK, atualizado.StatusCode);
        var cliente = await atualizado.Content.ReadFromJsonAsync<ClienteResult>();
        Assert.Equal(("Cliente Atualizado", "novo@example.com", false), (cliente!.Nome, cliente.Email, cliente.Ativo));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/clientes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/clientes/{id}")).StatusCode);
    }

    [Fact]
    public async Task Clientes_InexistenteEValidacao()
    {
        await AutenticarAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/clientes/999999", new { Nome = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/clientes/999999")).StatusCode);
        // FluentValidation responde antes do caso de uso.
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/api/clientes/1", new { Nome = "" })).StatusCode);
    }

    // ───────────── Veículos ─────────────

    [Fact]
    public async Task Veiculos_CrudCompleto()
    {
        await AutenticarAsync();
        var clienteId = await CriarClienteAsync();
        var placa = Placa();

        var criado = await _client.PostAsJsonAsync("/api/veiculos",
            new { Placa = placa.ToLowerInvariant(), Marca = "Honda", Modelo = "Civic", Ano = 2019, Cor = "Preto", ClienteId = clienteId });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var veiculo = (await criado.Content.ReadFromJsonAsync<VeiculoResult>())!;
        Assert.Equal(placa, veiculo.Placa);
        Assert.Equal($"/api/veiculos/{veiculo.Id}", criado.Headers.Location!.AbsolutePath);

        Assert.Contains((await _client.GetFromJsonAsync<List<VeiculoResult>>("/api/veiculos"))!, v => v.Id == veiculo.Id);
        Assert.Single((await _client.GetFromJsonAsync<List<VeiculoResult>>($"/api/veiculos/cliente/{clienteId}"))!);
        Assert.Equal("Civic", (await _client.GetFromJsonAsync<VeiculoResult>($"/api/veiculos/{veiculo.Id}"))!.Modelo);

        var atualizado = await _client.PutAsJsonAsync($"/api/veiculos/{veiculo.Id}",
            new { Marca = "Honda", Modelo = "City", Ano = 2021, Cor = "Branco" });
        Assert.Equal(HttpStatusCode.OK, atualizado.StatusCode);
        Assert.Equal(("City", 2021, "Branco"),
            ((await atualizado.Content.ReadFromJsonAsync<VeiculoResult>())!) is var v ? (v.Modelo, v.Ano, v.Cor) : default);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/veiculos/{veiculo.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/veiculos/{veiculo.Id}")).StatusCode);
    }

    [Fact]
    public async Task Veiculos_PlacaDuplicadaClienteInexistenteEInvalidos()
    {
        await AutenticarAsync();
        var clienteId = await CriarClienteAsync();
        var placa = Placa();
        await _client.PostAsJsonAsync("/api/veiculos", new { Placa = placa, Marca = "Fiat", Modelo = "Uno", Ano = 2010, ClienteId = clienteId });

        var duplicada = await _client.PostAsJsonAsync("/api/veiculos", new { Placa = placa, Marca = "Fiat", Modelo = "Uno", Ano = 2010, ClienteId = clienteId });
        var semCliente = await _client.PostAsJsonAsync("/api/veiculos", new { Placa = Placa(), Marca = "Fiat", Modelo = "Uno", Ano = 2010, ClienteId = 999999 });
        var placaInvalida = await _client.PostAsJsonAsync("/api/veiculos", new { Placa = "12", Marca = "Fiat", Modelo = "Uno", Ano = 2010, ClienteId = clienteId });

        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, semCliente.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, placaInvalida.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/veiculos/cliente/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/veiculos/999999", new { Marca = "X", Modelo = "Y", Ano = 2020 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/veiculos/999999")).StatusCode);
    }

    // ───────────── Peças ─────────────

    [Fact]
    public async Task Pecas_CrudCompletoEEstoqueBaixo()
    {
        await AutenticarAsync();
        var codigo = $"P-{Guid.NewGuid():N}"[..12];

        var criada = await _client.PostAsJsonAsync("/api/pecas",
            new { Nome = "Pastilha de freio", Codigo = codigo, PrecoUnitario = 89.90m, QuantidadeEstoque = 1, QuantidadeMinima = 5, Ativo = true });
        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        var peca = (await criada.Content.ReadFromJsonAsync<PecaResult>())!;
        Assert.True(peca.EstoqueBaixo);

        Assert.Contains((await _client.GetFromJsonAsync<List<PecaResult>>("/api/pecas/estoque-baixo"))!, p => p.Id == peca.Id);
        Assert.Contains((await _client.GetFromJsonAsync<List<PecaResult>>("/api/pecas"))!, p => p.Id == peca.Id);

        var atualizada = await _client.PutAsJsonAsync($"/api/pecas/{peca.Id}",
            new { Nome = "Pastilha de freio dianteira", Codigo = codigo, PrecoUnitario = 99.90m, QuantidadeEstoque = 20, QuantidadeMinima = 5, Ativo = true });
        Assert.Equal(HttpStatusCode.OK, atualizada.StatusCode);
        Assert.False((await atualizada.Content.ReadFromJsonAsync<PecaResult>())!.EstoqueBaixo);
        Assert.DoesNotContain((await _client.GetFromJsonAsync<List<PecaResult>>("/api/pecas/estoque-baixo"))!, p => p.Id == peca.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/pecas/{peca.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/pecas/{peca.Id}")).StatusCode);
    }

    [Fact]
    public async Task Pecas_CodigoDuplicadoEInexistente()
    {
        await AutenticarAsync();
        var codigo = $"D-{Guid.NewGuid():N}"[..12];
        var outro = $"E-{Guid.NewGuid():N}"[..12];
        await _client.PostAsJsonAsync("/api/pecas", new { Nome = "A", Codigo = codigo, PrecoUnitario = 1m, QuantidadeEstoque = 1, QuantidadeMinima = 0, Ativo = true });
        var segunda = await (await _client.PostAsJsonAsync("/api/pecas",
            new { Nome = "B", Codigo = outro, PrecoUnitario = 1m, QuantidadeEstoque = 1, QuantidadeMinima = 0, Ativo = true })).Content.ReadFromJsonAsync<IdResult>();

        var duplicadaNaCriacao = await _client.PostAsJsonAsync("/api/pecas",
            new { Nome = "C", Codigo = codigo, PrecoUnitario = 1m, QuantidadeEstoque = 1, QuantidadeMinima = 0, Ativo = true });
        var duplicadaNaAtualizacao = await _client.PutAsJsonAsync($"/api/pecas/{segunda!.Id}",
            new { Nome = "B", Codigo = codigo, PrecoUnitario = 1m, QuantidadeEstoque = 1, QuantidadeMinima = 0, Ativo = true });

        Assert.Equal(HttpStatusCode.Conflict, duplicadaNaCriacao.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicadaNaAtualizacao.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/pecas/999999",
            new { Nome = "X", PrecoUnitario = 1m, QuantidadeEstoque = 0, QuantidadeMinima = 0, Ativo = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/pecas/999999")).StatusCode);
    }

    // ───────────── Serviços ─────────────

    [Fact]
    public async Task Servicos_CrudCompleto()
    {
        await AutenticarAsync();

        var criado = await _client.PostAsJsonAsync("/api/servicos",
            new { Nome = "Balanceamento", Descricao = "4 rodas", PrecoBase = 60m, TempoEstimadoHoras = 0.5m, Ativo = true });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var servico = (await criado.Content.ReadFromJsonAsync<ServicoResult>())!;

        Assert.Contains((await _client.GetFromJsonAsync<List<ServicoResult>>("/api/servicos"))!, s => s.Id == servico.Id);
        Assert.Equal(60m, (await _client.GetFromJsonAsync<ServicoResult>($"/api/servicos/{servico.Id}"))!.PrecoBase);

        var atualizado = await _client.PutAsJsonAsync($"/api/servicos/{servico.Id}",
            new { Nome = "Balanceamento", PrecoBase = 70m, TempoEstimadoHoras = 1m, Ativo = false });
        Assert.Equal(HttpStatusCode.OK, atualizado.StatusCode);
        var s2 = (await atualizado.Content.ReadFromJsonAsync<ServicoResult>())!;
        Assert.Equal((70m, false), (s2.PrecoBase, s2.Ativo));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/servicos/{servico.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/servicos/{servico.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/servicos/999999",
            new { Nome = "X", PrecoBase = 1m, Ativo = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/servicos/999999")).StatusCode);
    }

    [Fact]
    public void DesignTimeFactory_CriaContextoMySqlSemConectar()
    {
        using var db = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        Assert.Equal("Pomelo.EntityFrameworkCore.MySql", db.Database.ProviderName);
    }
}
