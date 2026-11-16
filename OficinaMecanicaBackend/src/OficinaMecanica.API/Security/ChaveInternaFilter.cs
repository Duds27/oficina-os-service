using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace OficinaMecanica.API.Security;

/// <summary>Seção <c>InternalApi</c>. Em produção, a chave vem do Secrets Manager (variável <c>InternalApi__Key</c>).</summary>
public class InternalApiOptions
{
    public const string Secao = "InternalApi";
    public const string Header = "X-Internal-Key";

    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// Protege os endpoints internos (chamados pela Lambda de autenticação, fora do API Gateway)
/// com a chave do header <c>X-Internal-Key</c>. Sem chave configurada, recusa tudo.
/// </summary>
public class ChaveInternaFilter : IAuthorizationFilter
{
    private readonly IOptions<InternalApiOptions> _opcoes;
    private readonly ILogger<ChaveInternaFilter> _logger;

    public ChaveInternaFilter(IOptions<InternalApiOptions> opcoes, ILogger<ChaveInternaFilter> logger)
    {
        _opcoes = opcoes;
        _logger = logger;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var esperada = _opcoes.Value.Key;
        if (string.IsNullOrEmpty(esperada))
        {
            _logger.LogWarning("Endpoint interno chamado sem InternalApi:Key configurada; acesso recusado.");
            context.Result = new UnauthorizedResult();
            return;
        }

        var recebida = context.HttpContext.Request.Headers[InternalApiOptions.Header].ToString();
        if (!IguaisEmTempoConstante(recebida, esperada))
            context.Result = new UnauthorizedResult();
    }

    private static bool IguaisEmTempoConstante(string recebida, string esperada) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(recebida), Encoding.UTF8.GetBytes(esperada));
}

/// <summary>Aplica o <see cref="ChaveInternaFilter"/> ao controller ou à action.</summary>
public class ChaveInternaAttribute : TypeFilterAttribute
{
    public ChaveInternaAttribute() : base(typeof(ChaveInternaFilter)) { }
}
