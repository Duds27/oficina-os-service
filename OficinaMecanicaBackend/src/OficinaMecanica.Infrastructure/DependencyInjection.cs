using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Application.Abstractions;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Infrastructure.Data;
using OficinaMecanica.Infrastructure.Notifications;
using OficinaMecanica.Infrastructure.Persistence;
using OficinaMecanica.Infrastructure.Security;

namespace OficinaMecanica.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseMySql(
                connectionString,
                new MySqlServerVersion(new Version(8, 4, 0)),
                mysql => mysql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name)));

        services.AddPersistencia();
        services.AddSingleton<IAutenticador, JwtAutenticador>();

        return services;
    }

    /// <summary>
    /// Repositórios, unidade de trabalho e notificador sobre o AppDbContext já registrado.
    /// Separado do provedor do banco para os testes BDD usarem o mesmo código com SQLite.
    /// </summary>
    public static IServiceCollection AddPersistencia(this IServiceCollection services)
    {
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IClienteRepository, ClienteRepository>();
        services.AddScoped<IVeiculoRepository, VeiculoRepository>();
        services.AddScoped<IPecaRepository, PecaRepository>();
        services.AddScoped<IServicoRepository, ServicoRepository>();
        services.AddScoped<IOrdemServicoRepository, OrdemServicoRepository>();
        services.AddScoped<IConsultaSagaOs, ConsultaSagaOs>();

        services.AddScoped<INotificadorStatus, EmailNotificadorStatus>();

        return services;
    }
}
