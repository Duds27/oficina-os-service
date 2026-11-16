using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.Application.DTOs.OrdensServico;
using OficinaMecanica.Application.UseCases;

namespace OficinaMecanica.API.Controllers;

[ApiController]
[Route("api/ordens-servico")]
[Authorize]
public class OrdensServicoController : ControllerBase
{
    private readonly OrdemServicoUseCases _useCases;

    public OrdensServicoController(OrdemServicoUseCases useCases)
    {
        _useCases = useCases;
    }

    /// <summary>
    /// Lista as ordens de serviço ativas, ordenadas por prioridade de status
    /// (Em Execução → Aguardando Pagamento → Aguardando Aprovação → Em Diagnóstico → Recebida)
    /// e, dentro do mesmo status, das mais antigas para as mais recentes. OS finalizadas,
    /// entregues e canceladas são omitidas, exceto quando <paramref name="incluirConcluidas"/> = true.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool incluirConcluidas = false)
    {
        var result = await _useCases.ListarAsync(incluirConcluidas);
        return Ok(result.Data);
    }

    /// <summary>
    /// Retorna o tempo médio de execução das ordens de serviço, calculado do
    /// início da execução até a finalização. Considera apenas ordens finalizadas.
    /// </summary>
    [HttpGet("tempo-medio-execucao")]
    [ProducesResponseType(typeof(TempoMedioExecucaoDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTempoMedioExecucao()
    {
        var result = await _useCases.CalcularTempoMedioExecucaoAsync();
        return Ok(result.Data);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var result = await _useCases.ObterAsync(id);
        return result.StatusCode switch
        {
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }

    [HttpGet("{id:int}/status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStatus(int id)
    {
        var result = await _useCases.ObterStatusPublicoAsync(id);
        return result.StatusCode switch
        {
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateOrdemServicoDto dto)
    {
        var result = await _useCases.CriarAsync(dto);
        return result.StatusCode switch
        {
            201 => CreatedAtAction(nameof(GetById), new { id = result.Data!.Id }, result.Data),
            400 => BadRequest(new { error = result.Error }),
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }

    /// <summary>
    /// Entrega a OS ao cliente (Finalizada → Entregue). As outras mudanças de status são
    /// feitas pela saga, em resposta aos eventos do execution-service e do billing-service.
    /// </summary>
    [HttpPost("{id:int}/entregar")]
    [ProducesResponseType(typeof(OrdemServicoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Entregar(int id)
    {
        var result = await _useCases.EntregarAsync(id);
        return result.StatusCode switch
        {
            400 => BadRequest(new { error = result.Error }),
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }

    /// <summary>
    /// Histórico de status da OS, do mais antigo para o mais recente, com a origem de cada
    /// mudança (Usuario ou Saga) e o motivo.
    /// </summary>
    [HttpGet("{id:int}/historico")]
    [ProducesResponseType(typeof(IEnumerable<HistoricoStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistorico(int id)
    {
        var result = await _useCases.ListarHistoricoAsync(id);
        return result.StatusCode switch
        {
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }

    /// <summary>
    /// Estado atual da saga da OS: etapa, prazo, ids do orçamento e do pagamento e motivo do
    /// cancelamento. Endpoint administrativo, para suporte e demonstração.
    /// </summary>
    [HttpGet("{id:int}/saga")]
    [ProducesResponseType(typeof(SagaOrdemServicoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSaga(int id, [FromServices] SagaOrdemServicoUseCases sagas)
    {
        var result = await sagas.ObterAsync(id);
        return result.StatusCode switch
        {
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }

    /// <summary>
    /// Pede o cancelamento da OS. Responde 202: a saga aplica a compensação do estado atual
    /// (devolve peças, cancela orçamento e execução, estorna o pagamento se já foi pago).
    /// OS em execução ou já encerrada não pode ser cancelada (409).
    /// </summary>
    [HttpPost("{id:int}/cancelar")]
    [ProducesResponseType(typeof(CancelamentoSolicitadoDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancelar(int id, [FromBody] CancelarOrdemServicoDto? dto)
    {
        var solicitadoPor = User.Identity?.Name ?? "usuario-autenticado";
        var result = await _useCases.CancelarAsync(id, dto?.Motivo, solicitadoPor);
        return result.StatusCode switch
        {
            404 => NotFound(new { error = result.Error }),
            409 => Conflict(new { error = result.Error }),
            _   => AcceptedAtAction(nameof(GetSaga), new { id }, result.Data)
        };
    }

    [HttpPost("{id:int}/itens")]
    public async Task<IActionResult> AddItem(int id, [FromBody] AddItemDto dto)
    {
        var result = await _useCases.AdicionarItemAsync(id, dto);
        return result.StatusCode switch
        {
            201 => CreatedAtAction(nameof(GetById), new { id = result.Data!.Id }, result.Data),
            400 => BadRequest(new { error = result.Error }),
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }

    [HttpDelete("{id:int}/itens/{itemId:int}")]
    public async Task<IActionResult> DeleteItem(int id, int itemId)
    {
        var result = await _useCases.RemoverItemAsync(id, itemId);
        return result.StatusCode switch
        {
            404 => NotFound(new { error = result.Error }),
            _   => Ok(result.Data)
        };
    }
}
