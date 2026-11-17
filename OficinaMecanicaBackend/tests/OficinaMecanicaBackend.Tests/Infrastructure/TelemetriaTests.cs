using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Oficina.Contracts;
using OficinaMecanica.API.Observabilidade;
using OficinaMecanica.Infrastructure.Messaging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.OpenTelemetry;

namespace OficinaMecanicaBackend.Tests.Infrastructure;

/// <summary>Configuração do OpenTelemetry e dos logs (Etapa 5).</summary>
public class TelemetriaTests
{
    private static IConfiguration Config(params (string Chave, string? Valor)[] valores) =>
        new ConfigurationBuilder().AddInMemoryCollection(valores.Select(v => new KeyValuePair<string, string?>(v.Chave, v.Valor))).Build();

    [Fact]
    public void SemEndpoint_NaoExporta()
    {
        Assert.Null(Telemetria.EndpointOtlp(Config()));
        Assert.Null(Telemetria.EndpointOtlp(Config(("OTEL_EXPORTER_OTLP_ENDPOINT", ""))));
        Assert.Equal("http://coletor:4317", Telemetria.EndpointOtlp(Config(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://coletor:4317"))));
    }

    [Fact]
    public void Cabecalhos_SeguemOFormatoDoOpenTelemetry()
    {
        var cabecalhos = Telemetria.Cabecalhos("api-key=abc%3D123, outro = valor ,sem-igual");

        Assert.Equal("abc=123", cabecalhos["api-key"]);
        Assert.Equal("valor", cabecalhos["outro"]);
        Assert.Equal(2, cabecalhos.Count);
        Assert.Empty(Telemetria.Cabecalhos(null));
    }

    [Fact]
    public void HttpProtobuf_GanhaOCaminhoDosLogs()
    {
        Assert.Equal(OtlpProtocol.Grpc, Telemetria.Protocolo(Config()));
        Assert.Equal(OtlpProtocol.HttpProtobuf, Telemetria.Protocolo(Config(("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"))));
        Assert.Equal("https://otlp.nr-data.net:4318/v1/logs", Telemetria.CaminhoDeLogs("https://otlp.nr-data.net:4318/"));
        Assert.Equal("http://c:4318/v1/logs", Telemetria.CaminhoDeLogs("http://c:4318/v1/logs"));
    }

    [Fact]
    public void Ambiente_VemDaConfiguracaoOuDoAspNet()
    {
        var host = new AmbienteFalso("Production");

        Assert.Equal("homolog", Telemetria.Ambiente(Config(("Observabilidade:Ambiente", "homolog")), host));
        Assert.Equal("production", Telemetria.Ambiente(Config(), host));
    }

    [Theory]
    [InlineData("/health/ready", true)]
    [InlineData("/health/live", true)]
    [InlineData("/swagger/index.html", true)]
    [InlineData("/api/ordens-servico", false)]
    public void HealthESwagger_NaoViramSpan(string caminho, bool ignorada) =>
        Assert.Equal(ignorada, Telemetria.RotaIgnorada(new PathString(caminho)));

    [Fact]
    public void LogDentroDeUmSpan_LevaTraceIdESpanId()
    {
        using var atividade = new Activity("teste").SetIdFormat(ActivityIdFormat.W3C).Start();
        var evento = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("oi"), [], atividade.TraceId, atividade.SpanId);

        new ContextoDoTrace().Enrich(evento, new FabricaDePropriedades());

        Assert.Equal(atividade.TraceId.ToHexString(), ((ScalarValue)evento.Properties["trace.id"]).Value);
        Assert.Equal(atividade.SpanId.ToHexString(), ((ScalarValue)evento.Properties["span.id"]).Value);
    }

    [Fact]
    public void LogForaDeSpan_NaoGanhaTraceId()
    {
        var evento = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse("oi"), []);

        new ContextoDoTrace().Enrich(evento, new FabricaDePropriedades());

        Assert.False(evento.Properties.ContainsKey("trace.id"));
    }

    [Fact]
    public void FiltroDoConsumo_PoeCorrelationIdENumeroOsDasMensagensDaSaga()
    {
        var correlationId = Guid.NewGuid();
        var mensagem = new DiagnosticoIniciado { CorrelationId = correlationId, OsId = 1, NumeroOS = "OS-2026-000001", Mecanico = "Carlos" };

        var propriedades = Propriedades(ContextoDeLogConsumeFilter<DiagnosticoIniciado>.Propriedades(mensagem));

        Assert.Equal("DiagnosticoIniciado", propriedades["MessageType"]);
        Assert.Equal(correlationId, propriedades["CorrelationId"]);
        Assert.Equal("OS-2026-000001", propriedades["NumeroOS"]);
    }

    [Fact]
    public void FiltroDoConsumo_OutrasMensagensSoLevamOTipo()
    {
        var propriedades = Propriedades(ContextoDeLogConsumeFilter<string>.Propriedades("qualquer"));

        Assert.Equal(["MessageType"], propriedades.Keys);
    }

    private static Dictionary<string, object?> Propriedades(ILogEventEnricher[] enrichers)
    {
        var evento = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse("x"), []);
        foreach (var e in enrichers)
            e.Enrich(evento, new FabricaDePropriedades());
        return evento.Properties.ToDictionary(p => p.Key, p => ((ScalarValue)p.Value).Value);
    }

    private sealed class FabricaDePropriedades : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }

    private sealed class AmbienteFalso(string nome) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nome;
        public string ApplicationName { get; set; } = "teste";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
