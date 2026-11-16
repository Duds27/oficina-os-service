using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Application.Services;
using OficinaMecanica.Application.UseCases;

namespace OficinaMecanica.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ClienteUseCases>();
        services.AddScoped<VeiculoUseCases>();
        services.AddScoped<PecaUseCases>();
        services.AddScoped<ServicoUseCases>();
        services.AddScoped<OrdemServicoUseCases>();
        services.AddScoped<SagaOrdemServicoUseCases>();
        services.AddScoped<AutenticarUseCase>();
        services.AddScoped<EstoqueService>();
        services.AddScoped<IOperacoesOsSaga, OperacoesOsSaga>();
        return services;
    }
}
