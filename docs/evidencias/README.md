# Evidências de cobertura

Relatório do próprio CI da `main` (job `build-test-quality`), consolidado pelo ReportGenerator a partir de
todos os projetos de teste. O gate do CI exige 80% de linhas, e o SonarCloud tem o quality gate dele.

| | |
| --- | --- |
| Cobertura de linhas | **95%** (1843 de 1940 linhas) |
| Cobertura de ramos | 80.8% |
| Commit | [`281638a`](https://github.com/Duds27/oficina-os-service/commit/281638a) |
| Run do CI | [44062097216](https://github.com/Duds27/oficina-os-service/actions/runs/44062097216) (artifact `coverage`) |
| SonarCloud | [quality gate e cobertura](https://sonarcloud.io/summary/new_code?id=Duds27_oficina-os-service) |

![Cobertura de linhas](badge_linecoverage.svg) ![Cobertura de ramos](badge_branchcoverage.svg)

O detalhe por assembly e classe está em [`cobertura-resumo.md`](cobertura-resumo.md). O relatório
completo, mais novo, sai em todo run do CI, no artifact `coverage`.
