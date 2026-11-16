using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OficinaMecanicaBackend.Tests.Infrastructure;

namespace OficinaMecanicaBackend.Tests.Integration;

/// <summary>Endpoint interno da Lambda de autenticação: GET /internal/clientes/por-cpf/{cpf}.</summary>
[Collection("Integration")]
public class InternoClientesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public InternoClientesControllerTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private record IdResult(int Id);
    private record ClienteInternoResult(int Id, string Nome, bool Ativo);

    private async Task<(int Id, string Cpf)> CriarClienteAsync()
    {
        var token = await CustomWebApplicationFactory.GetAuthTokenAsync(_client);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/clientes");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var cpf = CenarioSaga.GerarCpf();
        req.Content = JsonContent.Create(new { CpfCnpj = cpf, Nome = "Cliente Lambda" });
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return ((await resp.Content.ReadFromJsonAsync<IdResult>())!.Id, cpf);
    }

    private Task<HttpResponseMessage> GetPorCpfAsync(string cpf, string? chave)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/internal/clientes/por-cpf/{cpf}");
        if (chave is not null)
            req.Headers.Add("X-Internal-Key", chave);
        return _client.SendAsync(req);
    }

    [Fact]
    public async Task ComChave_RetornaIdNomeEAtivo()
    {
        var (id, cpf) = await CriarClienteAsync();

        var resp = await GetPorCpfAsync(cpf, CustomWebApplicationFactory.ChaveInterna);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var cliente = await resp.Content.ReadFromJsonAsync<ClienteInternoResult>();
        Assert.Equal(new ClienteInternoResult(id, "Cliente Lambda", true), cliente);
    }

    [Fact]
    public async Task CpfComMascara_EncontraOMesmoCliente()
    {
        var (id, cpf) = await CriarClienteAsync();
        var comMascara = $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";

        var resp = await GetPorCpfAsync(Uri.EscapeDataString(comMascara), CustomWebApplicationFactory.ChaveInterna);

        Assert.Equal(id, (await resp.Content.ReadFromJsonAsync<ClienteInternoResult>())!.Id);
    }

    [Fact]
    public async Task CpfDesconhecido_Retorna404()
    {
        var resp = await GetPorCpfAsync("00000000191", CustomWebApplicationFactory.ChaveInterna);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chave-errada")]
    public async Task SemChaveOuChaveErrada_Retorna401(string? chave)
    {
        var (_, cpf) = await CriarClienteAsync();

        var resp = await GetPorCpfAsync(cpf, chave);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task JwtNaoSubstituiAChaveInterna()
    {
        var (_, cpf) = await CriarClienteAsync();
        var token = await CustomWebApplicationFactory.GetAuthTokenAsync(_client);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/internal/clientes/por-cpf/{cpf}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
