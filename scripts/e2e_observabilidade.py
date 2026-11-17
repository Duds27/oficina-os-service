"""Confere a observabilidade de ponta a ponta no docker-compose.full.yml (perfil observabilidade).

    OTEL_ENDPOINT=http://otel-collector:4317 docker compose -f docker-compose.full.yml --profile observabilidade up --build -d --wait
    python scripts/e2e_observabilidade.py

1. Roda a saga (scripts/e2e_saga.py) e guarda o número da OS.
2. Traces (Jaeger): um trace com spans dos 3 serviços. É o da conclusão do diagnóstico: o
   execution grava no outbox do DynamoDB, a saga do os-service consome e manda GerarOrcamento,
   e o billing responde com OrcamentoGerado.
3. Métricas (coletor, formato Prometheus): as de negócio dos 3 serviços.
4. Logs (observabilidade/saida/logs.jsonl, gravado pelo coletor): logs da OS nos 3 serviços,
   achados pelo NumeroOS, com trace id.

Sai com código 1 se algo faltar.
"""
import datetime
import json
import os
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.error
import urllib.request

AQUI = os.path.dirname(os.path.abspath(__file__))
COMPOSE = os.path.join(os.path.dirname(AQUI), "docker-compose.full.yml")
JAEGER = "http://localhost:16686"
METRICAS = "http://localhost:8889/metrics"
SERVICOS = {"os-service", "billing-service", "execution-service"}

# Prefixos no formato Prometheus (o coletor troca "." por "_" e acrescenta unidade/_total).
METRICAS_DE_NEGOCIO = {
    "os-service": ["saga_iniciada", "saga_concluida", "saga_etapa_duracao", "saga_prazos_atrasados"],
    "billing-service": ["orcamento_decisao", "pagamento_resultado"],
    "execution-service": ["execucao_duracao", "execucao_fila_tamanho"],
}

falhas = 0


def check(nome, ok, detalhe=""):
    global falhas
    print(("OK  " if ok else "FAIL"), nome, f"- {detalhe}" if detalhe else "")
    falhas += 0 if ok else 1


def get_json(url):
    with urllib.request.urlopen(url, timeout=15) as r:
        return json.load(r)


def esperar(funcao, segundos=90):
    fim = time.time() + segundos
    while True:
        valor = funcao()
        if valor or time.time() > fim:
            return valor
        time.sleep(3)


def rodar_saga():
    saida = subprocess.run([sys.executable, "-X", "utf8", os.path.join(AQUI, "e2e_saga.py")],
                           capture_output=True, text=True, encoding="utf-8")
    print(saida.stdout.strip().splitlines()[-1] if saida.stdout else saida.stderr)
    if saida.returncode != 0:
        print(saida.stdout, saida.stderr)
        sys.exit("A saga falhou; a observabilidade não foi conferida.")
    numero = re.search(r"OK\s+1\..*?(OS-\d{4}-\d{6})", saida.stdout)
    return numero.group(1)


def traces_recentes():
    """
    Traces da última hora que passam pelo execution (onde começa o da conclusão do diagnóstico),
    pela API v3 do Jaeger (OTLP em JSON). Devolve {trace id: [(serviço, nome do span, início)]}.
    """
    agora = datetime.datetime.now(datetime.timezone.utc)
    consulta = urllib.parse.urlencode({
        "query.service_name": "execution-service",
        "query.start_time_min": (agora - datetime.timedelta(hours=1)).isoformat().replace("+00:00", "Z"),
        "query.start_time_max": (agora + datetime.timedelta(minutes=1)).isoformat().replace("+00:00", "Z"),
        "query.search_depth": "500",
    })
    try:
        dados = get_json(f"{JAEGER}/api/v3/traces?{consulta}")
    except urllib.error.HTTPError:
        return {}
    traces = {}
    for recurso in dados.get("result", {}).get("resourceSpans", []):
        servico = next(a["value"]["stringValue"] for a in recurso["resource"]["attributes"] if a["key"] == "service.name")
        for escopo in recurso.get("scopeSpans", []):
            for span in escopo.get("spans", []):
                traces.setdefault(span["traceId"], []).append((servico, span["name"], int(span["startTimeUnixNano"])))
    return traces


def trace_dos_tres():
    """O trace com spans dos 3 serviços que tem mais spans."""
    candidatos = [(tid, spans) for tid, spans in traces_recentes().items() if SERVICOS <= {s for s, _, _ in spans}]
    return max(candidatos, key=lambda c: len(c[1]), default=None)


def metricas():
    with urllib.request.urlopen(METRICAS, timeout=15) as r:
        texto = r.read().decode()
    faltando = [m for nomes in METRICAS_DE_NEGOCIO.values() for m in nomes
                if not re.search(rf"^{m}[a-z_]*\{{", texto, re.M)]
    return texto, faltando


def logs_da_os(numero):
    """Logs com o NumeroOS, por serviço, do arquivo que o coletor grava (pasta montada do host)."""
    arquivo = os.path.join(os.path.dirname(AQUI), "observabilidade", "saida", "logs.jsonl")
    por_servico = {}
    if not os.path.exists(arquivo):
        return por_servico
    with open(arquivo, encoding="utf-8") as f:
        for linha in f:
            for rl in json.loads(linha).get("resourceLogs", []):
                servico = next((a["value"].get("stringValue") for a in rl["resource"]["attributes"] if a["key"] == "service.name"), "?")
                for sl in rl.get("scopeLogs", []):
                    for registro in sl.get("logRecords", []):
                        atributos = {a["key"]: a["value"] for a in registro.get("attributes", [])}
                        if atributos.get("NumeroOS", {}).get("stringValue") == numero:
                            por_servico.setdefault(servico, []).append(registro)
    return por_servico


def main():
    numero = rodar_saga()
    print(f"OS da rodada: {numero}\n")

    achado = esperar(trace_dos_tres)
    if achado:
        trace_id, spans = achado
        spans = sorted(spans, key=lambda s: s[2])
        check("1. trace com spans dos 3 serviços", True, f"{trace_id} ({len(spans)} spans)")
        for servico, nome, _ in spans:
            print(f"       {servico:<18} {nome}")
        check("2. o outbox do execution continua o trace (span 'outbox publish')",
              any(nome.startswith("outbox publish") for _, nome, _ in spans))
    else:
        maior = max((len({s for s, _, _ in spans}) for spans in traces_recentes().values()), default=0)
        check("1. trace com spans dos 3 serviços", False, f"o maior trace tem {maior} serviço(s)")

    texto, faltando = esperar(lambda: (lambda r: r if not r[1] else None)(metricas()), 60) or metricas()
    check("3. métricas de negócio dos 3 serviços no coletor", not faltando, f"faltando: {faltando}" if faltando else
          ", ".join(sorted({m for ms in METRICAS_DE_NEGOCIO.values() for m in ms})))

    por_servico = esperar(lambda: (lambda l: l if SERVICOS <= set(l) else None)(logs_da_os(numero)), 60) or logs_da_os(numero)
    check("4. logs com o NumeroOS nos 3 serviços", SERVICOS <= set(por_servico),
          ", ".join(f"{s}: {len(r)}" for s, r in sorted(por_servico.items())))
    com_trace = all(any(r.get("traceId") for r in registros) for registros in por_servico.values())
    check("5. esses logs levam o trace id", bool(por_servico) and com_trace)

    print("\nTudo certo." if falhas == 0 else f"\n{falhas} falha(s).")
    sys.exit(1 if falhas else 0)


if __name__ == "__main__":
    main()
