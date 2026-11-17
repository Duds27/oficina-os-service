# Testes sem conta no New Relic: provider mockado.
# Rodar com: terraform init -backend=false && terraform test

mock_provider "newrelic" {}

# O id da política é numérico no provider; um diferente por política, para o teste de severidade.
override_resource {
  target = newrelic_alert_policy.oficina["critico"]
  values = { id = "101" }
}

override_resource {
  target = newrelic_alert_policy.oficina["alerta"]
  values = { id = "202" }
}

variables {
  newrelic_account_id = 1234567
  newrelic_api_key    = "NRAK-teste"
}

run "dashboard_com_as_paginas_e_a_conta_em_cada_consulta" {
  assert {
    condition     = [for p in jsondecode(newrelic_one_dashboard_json.saga.json).pages : p.name] == ["Saga", "Filas", "Serviços"]
    error_message = "Páginas Saga, Filas e Serviços."
  }

  assert {
    condition = alltrue(flatten([
      for p in jsondecode(newrelic_one_dashboard_json.saga.json).pages : [
        for w in p.widgets : [for q in w.rawConfiguration.nrqlQueries : q.accountIds == [1234567]]
      ]
    ]))
    error_message = "Toda consulta do dashboard aponta para a conta informada (o JSON versionado tem 0)."
  }

  assert {
    condition = length(setsubtract(
      [
        "Sagas iniciadas, concluídas e compensadas por dia",
        "Motivos de compensação",
        "Tempo médio por etapa da saga (min)",
        "Funil: OS aberta → orçamento aprovado → pago → finalizado",
        "Taxa de aprovação de orçamento e de pagamento (%)",
        "Mensagens nas filas _error (agora)",
        "Profundidade das filas",
        "Latência p95 por serviço (ms)",
        "Taxa de erro por serviço (%)"
      ],
      flatten([for p in jsondecode(newrelic_one_dashboard_json.saga.json).pages : [for w in p.widgets : w.title]])
    )) == 0
    error_message = "O dashboard tem os widgets pedidos na 5.5."
  }
}

run "alertas_da_tabela_da_etapa_5" {
  command = plan

  assert {
    condition     = length(newrelic_nrql_alert_condition.oficina) == 8
    error_message = "As 6 condições da 5.6, o log de falha de estorno e o alerta de falha de OS da Fase 3."
  }

  assert {
    condition = (
      newrelic_nrql_alert_condition.oficina["filas_error"].critical[0].threshold == 0 &&
      newrelic_nrql_alert_condition.oficina["filas_error"].critical[0].threshold_duration == 300 &&
      newrelic_nrql_alert_condition.oficina["estorno_falhou"].critical[0].threshold == 1 &&
      newrelic_nrql_alert_condition.oficina["compensacoes"].critical[0].threshold == 5 &&
      newrelic_nrql_alert_condition.oficina["compensacoes"].aggregation_window == 3600 &&
      newrelic_nrql_alert_condition.oficina["webhook_mp"].critical[0].threshold == 3 &&
      newrelic_nrql_alert_condition.oficina["webhook_mp"].aggregation_window == 600 &&
      newrelic_nrql_alert_condition.oficina["latencia"].critical[0].threshold == 1000 &&
      newrelic_nrql_alert_condition.oficina["saga_parada"].critical[0].threshold == 0
    )
    error_message = "Limiares da tabela da 5.6: _error > 0 por 5 min, estorno >= 1, compensações > 5/h, webhook > 3 em 10 min, p95 > 1 s, saga parada > 0."
  }

  assert {
    condition = (
      strcontains(newrelic_nrql_alert_condition.oficina["estorno_falhou"].nrql[0].query, "billing-estornar-pagamento_error") &&
      strcontains(newrelic_nrql_alert_condition.oficina["webhook_mp"].nrql[0].query, "webhooks/mercadopago") &&
      strcontains(newrelic_nrql_alert_condition.oficina["saga_parada"].nrql[0].query, "saga.prazos.atrasados")
    )
    error_message = "As consultas usam a fila do estorno, a rota do webhook e o gauge da saga."
  }
}

run "severidade_pela_politica_e_filtro_de_ambiente" {
  variables {
    ambiente = "prod"
  }

  assert {
    condition = alltrue([
      for k in ["filas_error", "estorno_falhou", "estorno_falhou_log"] :
      tostring(newrelic_nrql_alert_condition.oficina[k].policy_id) == newrelic_alert_policy.oficina["critico"].id
    ])
    error_message = "Filas _error e falha de estorno ficam na política crítica."
  }

  assert {
    condition = alltrue([
      for k, c in newrelic_nrql_alert_condition.oficina :
      strcontains(c.nrql[0].query, "deployment.environment = 'prod'") if strcontains(c.nrql[0].query, "service.name")
    ])
    error_message = "As condições sobre os serviços olham só o ambiente informado."
  }

  assert {
    condition     = !strcontains(newrelic_nrql_alert_condition.oficina["filas_error"].nrql[0].query, "deployment.environment")
    error_message = "As métricas do RabbitMQ (Prometheus) não têm deployment.environment."
  }

  assert {
    condition     = newrelic_alert_policy.oficina["alerta"].name == "Oficina Mecânica — alerta (prod)"
    error_message = "O nome da política leva o ambiente."
  }
}

run "sem_email_nao_cria_notificacao" {
  command = plan

  assert {
    condition     = length(newrelic_workflow.oficina) == 0 && length(newrelic_notification_destination.email) == 0
    error_message = "Sem email_alertas, os incidentes só aparecem no New Relic."
  }
}

run "com_email_notifica_as_duas_politicas" {
  variables {
    email_alertas = "equipe@example.com"
  }

  assert {
    condition     = length(newrelic_workflow.oficina) == 1 && length(flatten([for f in newrelic_workflow.oficina[0].issues_filter : [for pr in f.predicate : pr.values]])) == 2
    error_message = "Com e-mail, um workflow cobre as políticas crítica e de alerta."
  }
}

run "regiao_invalida" {
  command = plan

  variables {
    newrelic_region = "BR"
  }

  expect_failures = [var.newrelic_region]
}
