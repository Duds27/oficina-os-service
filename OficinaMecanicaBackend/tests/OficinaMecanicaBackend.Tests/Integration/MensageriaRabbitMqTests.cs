using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Infrastructure.Data;
using OficinaMecanicaBackend.Tests.Infrastructure;

namespace OficinaMecanicaBackend.Tests.Integration;

/// <summary>
/// Ponta a ponta dentro do os-service, com RabbitMQ 4.1 real: abertura da OS → outbox →
/// exchange do evento → fila da saga → saga no MySQL → comando na fila do execution-service.
/// </summary>
[Collection("Integration")]
public class MensageriaRabbitMqTests : IClassFixture<RabbitMqWebApplicationFactory>
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(30);

    private readonly RabbitMqWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MensageriaRabbitMqTests(RabbitMqWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private record IdResult(int Id);

    [Fact]
    public async Task HealthReady_ComBancoEBroker_Retorna200()
    {
        var limite = DateTime.UtcNow + Espera;
        HttpStatusCode status;
        do
        {
            status = (await _client.GetAsync("/health/ready")).StatusCode;
            if (status == HttpStatusCode.OK) break;
            await Task.Delay(500);
        } while (DateTime.UtcNow < limite);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task AberturaDaOS_ChegaAFilaDoExecutionComoSolicitarDiagnostico()
    {
        var token = await CustomWebApplicationFactory.GetAuthTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var cliente = await (await _client.PostAsJsonAsync("/api/clientes",
            new { CpfCnpj = "11144477735", Nome = "Cliente RabbitMQ" })).Content.ReadFromJsonAsync<IdResult>();
        var veiculo = await (await _client.PostAsJsonAsync("/api/veiculos",
            new { Placa = "RAB1T23", Marca = "VW", Modelo = "Gol", Ano = 2018, ClienteId = cliente!.Id })).Content.ReadFromJsonAsync<IdResult>();

        var resp = await _client.PostAsJsonAsync("/api/ordens-servico", new { ClienteId = cliente.Id, VeiculoId = veiculo!.Id });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var osId = (await resp.Content.ReadFromJsonAsync<IdResult>())!.Id;

        // O comando chega à fila do execution-service, no envelope do MassTransit.
        JsonElement? envelope = null;
        var limite = DateTime.UtcNow + Espera;
        while (envelope is null && DateTime.UtcNow < limite)
        {
            envelope = (await _factory.EspiarFilaAsync("execution-solicitar-diagnostico"))
                .Cast<JsonElement?>()
                .FirstOrDefault(e => e!.Value.GetProperty("message").GetProperty("osId").GetInt32() == osId);
            if (envelope is null) await Task.Delay(500);
        }
        Assert.NotNull(envelope);

        var tipos = envelope!.Value.GetProperty("messageType").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("urn:message:oficina:solicitar-diagnostico:v1", tipos);
        var mensagem = envelope.Value.GetProperty("message");
        Assert.Equal("RAB1T23", mensagem.GetProperty("veiculo").GetProperty("placa").GetString());

        // A saga ficou no MySQL, com o mesmo CorrelationId da OS.
        await using var escopo = _factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var correlationId = await db.OrdensServico.Where(o => o.Id == osId).Select(o => o.CorrelationId).SingleAsync();
        Assert.Equal(correlationId, mensagem.GetProperty("correlationId").GetGuid());
        var saga = await db.Set<OrdemServicoSagaState>().AsNoTracking().SingleAsync(s => s.CorrelationId == correlationId);
        Assert.Equal("AguardandoDiagnostico", saga.CurrentState);
    }

    [Fact]
    public async Task Topologia_UsaOsNomesDoContrato()
    {
        // Força a criação da topologia (o bus sobe com o host).
        await _client.GetAsync("/health/live");
        using var http = _factory.CriarClienteManagement();

        var limite = DateTime.UtcNow + Espera;
        List<string> exchanges;
        do
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"/api/exchanges/{RabbitMqWebApplicationFactory.VHost}"));
            exchanges = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();
            if (exchanges.Contains("oficina.ordem-servico-aberta.v1")) break;
            await Task.Delay(500);
        } while (DateTime.UtcNow < limite);

        // Eventos que a saga assina viram exchanges pelo EntityName; o envelope não.
        Assert.Contains("oficina.ordem-servico-aberta.v1", exchanges);
        Assert.Contains("oficina.diagnostico-iniciado.v1", exchanges);
        Assert.Contains("oficina.pagamento-estornado.v1", exchanges);
        Assert.Contains("os-ordem-servico-saga", exchanges);
        Assert.DoesNotContain(exchanges, e => e.Contains("MensagemOS") || e.Contains("IMensagemOS"));
    }
}
