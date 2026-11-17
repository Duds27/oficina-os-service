# Dashboard "Saga de OS". O JSON versionado (../newrelic-dashboard-saga.json) também serve para
# importar à mão (New Relic → Dashboards → Import), e lá as consultas vêm com accountIds [0].
# Aqui cada consulta recebe a conta.
locals {
  dashboard_base = jsondecode(file("${path.module}/../newrelic-dashboard-saga.json"))

  dashboard_saga = merge(local.dashboard_base, {
    pages = [
      for pagina in local.dashboard_base.pages : merge(pagina, {
        widgets = [
          for w in pagina.widgets : merge(w, {
            rawConfiguration = merge(w.rawConfiguration, {
              nrqlQueries = [for q in w.rawConfiguration.nrqlQueries : merge(q, { accountIds = [var.newrelic_account_id] })]
            })
          })
        ]
      })
    ]
  })
}

resource "newrelic_one_dashboard_json" "saga" {
  json = jsonencode(local.dashboard_saga)
}
