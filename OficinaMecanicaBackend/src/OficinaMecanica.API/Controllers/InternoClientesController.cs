using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.API.Security;
using OficinaMecanica.Application.DTOs.Clientes;
using OficinaMecanica.Application.UseCases;

namespace OficinaMecanica.API.Controllers;

/// <summary>
/// Endpoints internos, chamados por outros componentes da plataforma (não pelo cliente).
/// Não são roteados pelo API Gateway e exigem o header <c>X-Internal-Key</c>.
/// </summary>
[ApiController]
[Route("internal/clientes")]
[AllowAnonymous]
[ChaveInterna]
public class InternoClientesController : ControllerBase
{
    private readonly ClienteUseCases _useCases;

    public InternoClientesController(ClienteUseCases useCases) => _useCases = useCases;

    /// <summary>
    /// Cliente pelo CPF (com ou sem máscara). Usado pela Lambda de autenticação para emitir
    /// o JWT com o claim <c>clienteId</c>, sem acessar o banco do os-service.
    /// </summary>
    [HttpGet("por-cpf/{cpf}")]
    [ProducesResponseType(typeof(ClienteInternoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPorCpf(string cpf)
    {
        var result = await _useCases.ObterPorCpfCnpjAsync(cpf);
        return result.StatusCode switch
        {
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }
}
