# Summary
<details open><summary>Summary</summary>

|||
|:---|:---|
| Generated on: | 17/11/2026 - 07:54:51 |
| Coverage date: | 17/11/2026 - 07:12:20 |
| Parser: | Cobertura |
| Assemblies: | 4 |
| Classes: | 89 |
| Files: | 51 |
| **Line coverage:** | 95% (1843 of 1940) |
| Covered lines: | 1843 |
| Uncovered lines: | 97 |
| Coverable lines: | 1940 |
| Total lines: | 3754 |
| **Branch coverage:** | 80.8% (415 of 513) |
| Covered branches: | 415 |
| Total branches: | 513 |
| **Method coverage:** | [Feature is only available for sponsors](https://reportgenerator.io/pro) |

</details>

## Coverage
<details><summary>OficinaMecanica.API - 89.3%</summary>

|**Name**|**Line**|**Branch**|
|:---|---:|---:|
|**OficinaMecanica.API**|**89.3%**|**71.4%**|
|OficinaMecanica.API.Controllers.AuthController|85.7%|50%|
|OficinaMecanica.API.Controllers.ClientesController|94.5%|85.7%|
|OficinaMecanica.API.Controllers.InternoClientesController|100%|100%|
|OficinaMecanica.API.Controllers.OrdensServicoController|84.8%|47.2%|
|OficinaMecanica.API.Controllers.PecasController|97.4%|91.6%|
|OficinaMecanica.API.Controllers.ServicosController|97%|87.5%|
|OficinaMecanica.API.Controllers.VeiculosController|95.4%|77.7%|
|OficinaMecanica.API.Middleware.CorrelationIdMiddleware|100%|50%|
|OficinaMecanica.API.Observabilidade.ContextoDoTrace|100%|100%|
|OficinaMecanica.API.Observabilidade.Telemetria|76.9%|75%|
|OficinaMecanica.API.Security.ChaveInternaAttribute|100%||
|OficinaMecanica.API.Security.ChaveInternaFilter|76.9%|75%|

</details>
<details><summary>OficinaMecanica.Application - 96%</summary>

|**Name**|**Line**|**Branch**|
|:---|---:|---:|
|**OficinaMecanica.Application**|**96%**|**84.4%**|
|Oficina.Contracts.CancelamentoSolicitado|100%||
|Oficina.Contracts.CancelarExecucao|100%||
|Oficina.Contracts.CancelarOrcamento|100%||
|Oficina.Contracts.ClienteInfo|0%||
|Oficina.Contracts.DiagnosticoConcluido|0%||
|Oficina.Contracts.DiagnosticoIniciado|100%||
|Oficina.Contracts.EstornarPagamento|100%||
|Oficina.Contracts.ExecucaoFinalizada|100%||
|Oficina.Contracts.ExecucaoIniciada|0%||
|Oficina.Contracts.ExecucaoRejeitada|100%||
|Oficina.Contracts.FilasComando|100%||
|Oficina.Contracts.GerarOrcamento|100%||
|Oficina.Contracts.IniciarExecucao|100%||
|Oficina.Contracts.ItemOrcamentoInfo|0%||
|Oficina.Contracts.MensagemOS|100%||
|Oficina.Contracts.OrcamentoAprovado|100%||
|Oficina.Contracts.OrcamentoGerado|100%||
|Oficina.Contracts.OrcamentoRecusado|100%||
|Oficina.Contracts.OrdemServicoAberta|0%||
|Oficina.Contracts.PagamentoConfirmado|100%||
|Oficina.Contracts.PagamentoEstornado|100%||
|Oficina.Contracts.PagamentoRecusado|100%||
|Oficina.Contracts.PrazoExpirado|100%||
|Oficina.Contracts.ServicoExecucaoInfo|0%||
|Oficina.Contracts.SolicitarDiagnostico|100%||
|Oficina.Contracts.VeiculoInfo|0%||
|OficinaMecanica.Application.Abstractions.ConflitoPersistenciaException|0%||
|OficinaMecanica.Application.Common.ServiceResult`1|83.3%||
|OficinaMecanica.Application.DependencyInjection|100%||
|OficinaMecanica.Application.Saga.DadosOsSaga|0%||
|OficinaMecanica.Application.Saga.MetricasSaga|100%||
|OficinaMecanica.Application.Saga.OperacoesOsSaga|100%|72.7%|
|OficinaMecanica.Application.Saga.OrdemServicoStateMachine|98%|82.7%|
|OficinaMecanica.Application.Saga.PrazosSagaOptions|100%||
|OficinaMecanica.Application.Services.EstoqueService|100%|90%|
|OficinaMecanica.Application.UseCases.AutenticarUseCase|100%||
|OficinaMecanica.Application.UseCases.ClienteUseCases|98.4%|93.7%|
|OficinaMecanica.Application.UseCases.OrdemServicoUseCases|90.4%|81.5%|
|OficinaMecanica.Application.UseCases.PecaUseCases|100%|90.9%|
|OficinaMecanica.Application.UseCases.SagaOrdemServicoUseCases|100%|100%|
|OficinaMecanica.Application.UseCases.ServicoUseCases|100%|100%|
|OficinaMecanica.Application.UseCases.VeiculoUseCases|98.3%|88.8%|
|OficinaMecanica.Application.Validators.AddItemValidator|100%||
|OficinaMecanica.Application.Validators.CpfCnpjValidator|97.6%|82%|
|OficinaMecanica.Application.Validators.CreateClienteValidator|100%|100%|
|OficinaMecanica.Application.Validators.CreateOrdemServicoValidator|100%||
|OficinaMecanica.Application.Validators.CreatePecaValidator|100%||
|OficinaMecanica.Application.Validators.CreateServicoValidator|100%||
|OficinaMecanica.Application.Validators.CreateVeiculoValidator|100%|100%|
|OficinaMecanica.Application.Validators.PlacaValidator|100%|100%|
|OficinaMecanica.Application.Validators.UpdateClienteValidator|100%||
|OficinaMecanica.Application.Validators.UpdatePecaValidator|100%||
|OficinaMecanica.Application.Validators.UpdateServicoValidator|100%||
|OficinaMecanica.Application.Validators.UpdateVeiculoValidator|100%||

</details>
<details><summary>OficinaMecanica.Domain - 100%</summary>

|**Name**|**Line**|**Branch**|
|:---|---:|---:|
|**OficinaMecanica.Domain**|**100%**|**100%**|
|OficinaMecanica.Domain.Entities.OrdemServico|100%|100%|
|OficinaMecanica.Domain.Entities.Peca|100%||
|OficinaMecanica.Domain.Exceptions.TransicaoStatusInvalidaException|100%||

</details>
<details><summary>OficinaMecanica.Infrastructure - 95.9%</summary>

|**Name**|**Line**|**Branch**|
|:---|---:|---:|
|**OficinaMecanica.Infrastructure**|**95.9%**|**70.4%**|
|OficinaMecanica.Infrastructure.Data.AppDbContext|100%||
|OficinaMecanica.Infrastructure.Data.AppDbContextDesignTimeFactory|100%|100%|
|OficinaMecanica.Infrastructure.Data.OrdemServicoSagaStateMap|100%||
|OficinaMecanica.Infrastructure.DependencyInjection|100%||
|OficinaMecanica.Infrastructure.Messaging.ContextoDeLogConsumeFilter`1|91.6%|100%|
|OficinaMecanica.Infrastructure.Messaging.EstadoSagaLogFilter|75%||
|OficinaMecanica.Infrastructure.Messaging.MassTransitPublicadorEventos|100%||
|OficinaMecanica.Infrastructure.Messaging.MensageriaExtensions|100%|75%|
|OficinaMecanica.Infrastructure.Messaging.VerificadorPrazosSaga|90%|83.3%|
|OficinaMecanica.Infrastructure.Messaging.VerificadorPrazosSagaHostedService|62.5%|0%|
|OficinaMecanica.Infrastructure.Messaging.VerificadorPrazosSagaOptions|100%||
|OficinaMecanica.Infrastructure.Notifications.EmailNotificadorStatus|100%|50%|
|OficinaMecanica.Infrastructure.Persistence.ClienteRepository|100%|100%|
|OficinaMecanica.Infrastructure.Persistence.ConsultaSagaOs|100%||
|OficinaMecanica.Infrastructure.Persistence.OrdemServicoRepository|100%|75%|
|OficinaMecanica.Infrastructure.Persistence.PecaRepository|100%|100%|
|OficinaMecanica.Infrastructure.Persistence.ServicoRepository|100%|100%|
|OficinaMecanica.Infrastructure.Persistence.UnitOfWork|71.4%||
|OficinaMecanica.Infrastructure.Persistence.VeiculoRepository|100%|100%|
|OficinaMecanica.Infrastructure.Security.JwtAutenticador|96.5%|50%|

</details>
