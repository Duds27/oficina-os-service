using System.Text;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OficinaMecanica.API.Middleware;
using OficinaMecanica.API.Observabilidade;
using OficinaMecanica.API.Security;
using OficinaMecanica.Application;
using OficinaMecanica.Application.Validators;
using OficinaMecanica.Infrastructure;
using OficinaMecanica.Infrastructure.Data;
using OficinaMecanica.Infrastructure.Messaging;
using Serilog;
using Serilog.Formatting.Compact;

// Logs estruturados em JSON (CompactJsonFormatter) para ingestão no New Relic /
// agregadores de log, com correlação entre requisições via propriedade CorrelationId.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(new CompactJsonFormatter(), "logs/app-.log", rollingInterval: RollingInterval.Day)
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // preserveStaticLogger: cada host tem o próprio logger, e o estático (Log.Logger) fica só para
    // os erros de inicialização. Sem isso, o UseSerilog congela o logger estático, e duas subidas
    // ao mesmo tempo no mesmo processo (testes em paralelo) congelam o logger uma da outra: a
    // segunda falha e o Program termina sem construir o host.
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // trace.id/span.id em todo log e, com endpoint OTLP, o envio dos logs (Etapa 5).
        .ComTelemetria(ctx.Configuration, ctx.HostingEnvironment)
        .WriteTo.Console(new CompactJsonFormatter())
        .WriteTo.File(new CompactJsonFormatter(), "logs/app-.log", rollingInterval: RollingInterval.Day), preserveStaticLogger: true);

    // OpenTelemetry: traces (HTTP, HttpClient, MassTransit, MySQL) e métricas (HTTP, runtime,
    // MassTransit e a saga), exportados por OTLP quando OTEL_EXPORTER_OTLP_ENDPOINT existe.
    builder.Services.AddObservabilidade(builder.Configuration, builder.Environment);

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

    // Clean Architecture: composição das camadas
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(connectionString);
    // RabbitMQ + MassTransit (outbox/inbox no MySQL). O health check do bus entra no /health/ready.
    builder.Services.AddMensageria(builder.Configuration);
    // Chave dos endpoints internos (/internal/*), chamados pela Lambda de autenticação.
    builder.Services.Configure<InternalApiOptions>(builder.Configuration.GetSection(InternalApiOptions.Secao));

    // JWT Authentication
    var jwtKey = builder.Configuration["Jwt:Key"]
        ?? throw new InvalidOperationException("Jwt:Key não configurado.");
    var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
    var jwtAudience = builder.Configuration["Jwt:Audience"]!;

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtKey)),
                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,
                ValidateAudience = true,
                ValidAudience = jwtAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        });

    builder.Services.AddAuthorization();

    // Controllers + FluentValidation
    builder.Services.AddControllers();
    builder.Services.AddFluentValidationAutoValidation();
    builder.Services.AddValidatorsFromAssemblyContaining<CreateClienteValidator>();

    // Health checks (liveness sem dependências; readiness verifica o banco)
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>("db", tags: new[] { "ready" });

    // Swagger with JWT support
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "OficinaMecanica API", Version = "v1" });

        // Inclui os comentários XML (summaries) na documentação do Swagger.
        foreach (var xml in Directory.GetFiles(AppContext.BaseDirectory, "OficinaMecanica*.xml"))
            c.IncludeXmlComments(xml);

        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Description = "Cole apenas o token JWT (sem o prefixo 'Bearer ').",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT"
        });

        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                Array.Empty<string>()
            }
        });
    });

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational() && db.Database.ProviderName?.Contains("Sqlite") != true)
        {
            // O banco pode ainda estar subindo (ex.: MySQL em container na primeira
            // inicialização). Retenta a migração até o servidor aceitar conexões TCP,
            // em vez de derrubar a aplicação no primeiro erro transitório.
            const int maxAttempts = 12;
            const int delaySeconds = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    db.Database.Migrate();
                    break;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    Log.Warning(ex,
                        "Banco de dados indisponível (tentativa {Attempt}/{Max}). Nova tentativa em {Delay}s...",
                        attempt, maxAttempts, delaySeconds);
                    Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
                }
            }
        }
        else
        {
            db.Database.EnsureCreated();
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();
    // Correlação deve preceder o log de requisição para que o CorrelationId
    // apareça no evento de conclusão da requisição.
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
    });
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    // Liveness: processo de pé (nenhum check dependente executa).
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
        .AllowAnonymous();
    // Readiness: pronto para receber tráfego (banco e RabbitMQ acessíveis).
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") })
        .AllowAnonymous();

    app.Run();
}
catch (HostAbortedException)
{
    throw;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Expose for WebApplicationFactory in tests
public partial class Program { }
