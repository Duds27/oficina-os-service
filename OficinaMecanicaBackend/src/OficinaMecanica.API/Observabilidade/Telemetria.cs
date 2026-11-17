using System.Reflection;
using OficinaMecanica.Application.Saga;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace OficinaMecanica.API.Observabilidade;

/// <summary>
/// OpenTelemetry (traces e métricas) e o envio dos logs do Serilog por OTLP (Etapa 5).
///
/// A configuração segue as variáveis padrão do OpenTelemetry:
/// <list type="bullet">
/// <item><c>OTEL_EXPORTER_OTLP_ENDPOINT</c>: sem ele, nada é exportado (testes, compose sem
/// coletor). No EKS, o CD só o define junto com a license key do New Relic.</item>
/// <item><c>OTEL_EXPORTER_OTLP_HEADERS</c>: no New Relic, <c>api-key=&lt;license key&gt;</c>.</item>
/// <item><c>OTEL_EXPORTER_OTLP_PROTOCOL</c>: <c>grpc</c> (padrão) ou <c>http/protobuf</c>.</item>
/// </list>
/// Atributos do resource: <c>service.name</c>, <c>service.version</c> (variável
/// <c>SERVICE_VERSION</c>, o SHA do commit na imagem) e <c>deployment.environment</c>
/// (<c>Observabilidade:Ambiente</c>, ou o ambiente do ASP.NET).
/// </summary>
public static class Telemetria
{
    public const string NomeServico = "os-service";

    /// <summary>Fontes de spans além das instrumentações de ASP.NET Core e HttpClient.</summary>
    public static readonly string[] FontesDeTrace =
    [
        "MassTransit",    // publish, send e consume, com o contexto propagado nos headers da mensagem
        "MySqlConnector"  // comandos no MySQL (o driver já emite os spans)
    ];

    /// <summary>Meters exportados: os do MassTransit e o da saga.</summary>
    public static readonly string[] Meters = ["MassTransit", MetricasSaga.NomeMeter];

    public static IServiceCollection AddObservabilidade(this IServiceCollection services, IConfiguration configuration, IHostEnvironment host)
    {
        var exportar = EndpointOtlp(configuration) is not null;

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(NomeServico, serviceVersion: Versao())
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", Ambiente(configuration, host))]))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o =>
                    {
                        o.Filter = http => !RotaIgnorada(http.Request.Path);
                        o.RecordException = true;
                    })
                    .AddHttpClientInstrumentation(o => o.RecordException = true)
                    .AddSource(FontesDeTrace);
                if (exportar)
                    t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(Meters);
                if (exportar)
                    m.AddOtlpExporter();
            });

        return services;
    }

    /// <summary>
    /// Enriquecimento comum dos logs (trace.id e span.id) e, com endpoint OTLP, o envio dos logs
    /// por OTLP. O New Relic liga log e trace pelo trace id.
    /// </summary>
    public static LoggerConfiguration ComTelemetria(this LoggerConfiguration logs, IConfiguration configuration, IHostEnvironment host)
    {
        logs.Enrich.With<ContextoDoTrace>();

        if (EndpointOtlp(configuration) is { } endpoint)
        {
            var protocolo = Protocolo(configuration);
            logs.WriteTo.OpenTelemetry(o =>
            {
                o.Endpoint = protocolo == OtlpProtocol.HttpProtobuf ? CaminhoDeLogs(endpoint) : endpoint;
                o.Protocol = protocolo;
                o.Headers = Cabecalhos(configuration["OTEL_EXPORTER_OTLP_HEADERS"]);
                o.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = NomeServico,
                    ["service.version"] = Versao(),
                    ["deployment.environment"] = Ambiente(configuration, host)
                };
            }, ignoreEnvironment: true);
        }

        return logs;
    }

    public static string? EndpointOtlp(IConfiguration configuration) =>
        configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 } endpoint ? endpoint : null;

    public static string Versao() =>
        Environment.GetEnvironmentVariable("SERVICE_VERSION") is { Length: > 0 } versao
            ? versao
            : Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public static string Ambiente(IConfiguration configuration, IHostEnvironment host) =>
        configuration["Observabilidade:Ambiente"] is { Length: > 0 } ambiente ? ambiente : host.EnvironmentName.ToLowerInvariant();

    public static bool RotaIgnorada(PathString caminho) =>
        caminho.StartsWithSegments("/health") || caminho.StartsWithSegments("/swagger");

    public static OtlpProtocol Protocolo(IConfiguration configuration) =>
        string.Equals(configuration["OTEL_EXPORTER_OTLP_PROTOCOL"], "http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpProtocol.HttpProtobuf
            : OtlpProtocol.Grpc;

    /// <summary>No http/protobuf, o endpoint base ganha o caminho dos logs, como fazem os exportadores do OpenTelemetry.</summary>
    public static string CaminhoDeLogs(string endpoint) =>
        endpoint.TrimEnd('/').EndsWith("/v1/logs", StringComparison.Ordinal) ? endpoint : endpoint.TrimEnd('/') + "/v1/logs";

    /// <summary>Formato de <c>OTEL_EXPORTER_OTLP_HEADERS</c>: <c>chave=valor,chave2=valor2</c>, com valores URL-encoded.</summary>
    public static Dictionary<string, string> Cabecalhos(string? texto)
    {
        var cabecalhos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var par in (texto ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var igual = par.IndexOf('=');
            if (igual > 0)
                cabecalhos[par[..igual].Trim()] = Uri.UnescapeDataString(par[(igual + 1)..].Trim());
        }
        return cabecalhos;
    }
}

/// <summary>
/// Põe <c>trace.id</c> e <c>span.id</c> (os nomes que o New Relic usa para ligar log e trace)
/// em todo log escrito dentro de um span. Também vale para o log em JSON no console.
/// </summary>
public sealed class ContextoDoTrace : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.TraceId is { } trace)
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("trace.id", trace.ToHexString()));
        if (logEvent.SpanId is { } span)
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("span.id", span.ToHexString()));
    }
}
