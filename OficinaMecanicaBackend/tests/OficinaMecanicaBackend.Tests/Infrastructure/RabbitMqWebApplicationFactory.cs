using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Testcontainers.RabbitMq;

namespace OficinaMecanicaBackend.Tests.Infrastructure;

/// <summary>
/// API com MySQL e RabbitMQ reais (mesmas imagens do docker-compose), sem o test harness.
/// </summary>
public class RabbitMqWebApplicationFactory : CustomWebApplicationFactory
{
    public const string VHost = "oficina";
    private const string Usuario = "oficina";
    private const string Senha = "oficina_dev";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:4.1-management")
        .WithUsername(Usuario)
        .WithPassword(Senha)
        .WithEnvironment("RABBITMQ_DEFAULT_VHOST", VHost)
        .WithPortBinding(15672, true)
        .Build();

    protected override bool UsarTransporteEmMemoria => false;

    public override async Task InitializeAsync() =>
        await Task.WhenAll(base.InitializeAsync(), _rabbit.StartAsync());

    protected override Task PararContainersExtrasAsync() => _rabbit.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RabbitMq:Host", _rabbit.Hostname);
        builder.UseSetting("RabbitMq:Port", _rabbit.GetMappedPublicPort(5672).ToString());
        builder.UseSetting("RabbitMq:VirtualHost", VHost);
        builder.UseSetting("RabbitMq:Username", Usuario);
        builder.UseSetting("RabbitMq:Password", Senha);
    }

    /// <summary>Cliente da API de management do RabbitMQ.</summary>
    public HttpClient CriarClienteManagement()
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://{_rabbit.Hostname}:{_rabbit.GetMappedPublicPort(15672)}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Usuario}:{Senha}")));
        return http;
    }

    /// <summary>Lê (e devolve à fila) as mensagens de uma fila, pela API de management.</summary>
    public async Task<IReadOnlyList<JsonElement>> EspiarFilaAsync(string fila, int quantidade = 10)
    {
        using var http = CriarClienteManagement();
        var corpo = new StringContent(
            JsonSerializer.Serialize(new { count = quantidade, ackmode = "ack_requeue_true", encoding = "auto" }),
            Encoding.UTF8, "application/json");
        var resposta = await http.PostAsync($"/api/queues/{VHost}/{fila}/get", corpo);
        if (!resposta.IsSuccessStatusCode)
            return [];

        using var doc = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray()
            .Select(m => JsonDocument.Parse(m.GetProperty("payload").GetString()!).RootElement.Clone())
            .ToList();
    }
}
