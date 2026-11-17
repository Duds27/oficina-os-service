"""Saga de ponta a ponta nos 3 serviços: da abertura da OS até Concluida.

Roda contra o docker-compose.full.yml (portas padrão) ou qualquer ambiente com o gateway Fake
no billing-service. Usa os dados do seed.py, que é chamado antes (sem duplicar nada).

    python scripts/e2e_saga.py
    python scripts/e2e_saga.py --os http://localhost:8080 --billing http://localhost:8082 --execution http://localhost:8083

Sai com código 1 se algum passo falhar.
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import seed  # noqa: E402  (mesma pasta)

falhas = 0


def check(nome, ok, detalhe=""):
    global falhas
    print(("OK  " if ok else "FAIL"), nome, f"- {detalhe}" if detalhe else "")
    falhas += 0 if ok else 1


def esperar(funcao, segundos=60):
    fim = time.time() + segundos
    while time.time() < fim:
        valor = funcao()
        if valor:
            return valor
        time.sleep(0.5)
    return None


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--os", default="http://localhost:8080")
    p.add_argument("--billing", default="http://localhost:8082")
    p.add_argument("--execution", default="http://localhost:8083")
    p.add_argument("--usuario", default=os.environ.get("ADMIN_USER", "admin"))
    p.add_argument("--senha", default=os.environ.get("ADMIN_PASSWORD", "Admin@123"))
    args = p.parse_args()

    # Dados de exemplo e login do admin da equipe (o mesmo token vale nos 3 serviços).
    sys.argv = [sys.argv[0], "--url", args.os, "--usuario", args.usuario, "--senha", args.senha]
    seed.main()
    print()
    os_api, bill, exe = seed.Api(args.os), seed.Api(args.billing), seed.Api(args.execution)
    for api in (bill, exe):
        api.esperar_pronto(120)
    _, login = os_api.chamar("POST", "/api/auth/login", {"username": args.usuario, "password": args.senha})
    os_api.token = bill.token = exe.token = login["token"]

    cliente = next(c for c in os_api.listar("/api/clientes") if seed.so_digitos(c["cpfCnpj"]) == "52998224725")
    veiculo = next(v for v in os_api.listar("/api/veiculos") if seed.so_alfanumerico(v["placa"]) == "ABC1D23")
    servico = next(s for s in os_api.listar("/api/servicos") if s["nome"] == "Troca de pastilhas")
    peca = next(pc for pc in os_api.listar("/api/pecas") if pc["codigo"] == "PAST-DIANT")
    total_esperado = servico["precoBase"] + 2 * peca["precoUnitario"]

    s, ordem = os_api.chamar("POST", "/api/ordens-servico", {"clienteId": cliente["id"], "veiculoId": veiculo["id"], "descricao": "Freio fazendo barulho"})
    if s != 201:
        sys.exit(f"Abrir a OS respondeu {s}: {ordem}")
    os_id = ordem["id"]
    s1, _ = os_api.chamar("POST", f"/api/ordens-servico/{os_id}/itens", {"tipo": 0, "servicoId": servico["id"], "quantidade": 1})
    s2, _ = os_api.chamar("POST", f"/api/ordens-servico/{os_id}/itens", {"tipo": 1, "pecaId": peca["id"], "quantidade": 2})
    check("1. OS aberta com serviço e peça no os-service", s1 in (200, 201) and s2 in (200, 201), ordem["numeroOS"])

    def saga(estado):
        st, sg = os_api.chamar("GET", f"/api/ordens-servico/{os_id}/saga")
        return sg if st == 200 and isinstance(sg, dict) and sg.get("estado") == estado else None

    def execucao(etapa):
        st, e = exe.chamar("GET", f"/api/execucoes/{os_id}")
        return e if st == 200 and e.get("etapa") == etapa else None

    def orcamento():
        st, lista = bill.chamar("GET", f"/api/orcamentos?osId={os_id}")
        return lista[0] if st == 200 and lista else None

    check("2. execution abriu a execução (SolicitarDiagnostico)", bool(esperar(lambda: execucao("DiagnosticoPendente"))))
    exe.chamar("POST", f"/api/execucoes/{os_id}/diagnostico/iniciar")
    check("3. saga em EmDiagnostico (DiagnosticoIniciado)", bool(esperar(lambda: saga("EmDiagnostico"))))
    exe.chamar("POST", f"/api/execucoes/{os_id}/diagnostico/concluir", {"observacoes": "Pastilhas no fim"})
    sg = esperar(lambda: saga("AguardandoAprovacao"))
    check("4. billing gerou o orçamento (GerarOrcamento → OrcamentoGerado)", bool(sg), sg and f"orcamentoId {sg.get('orcamentoId')}")

    orc = esperar(orcamento)
    check("5. orçamento enviado com o total calculado", bool(orc) and orc["status"] == "Enviado" and abs(orc["valorTotal"] - total_esperado) < 0.001,
          orc and f"total {orc['valorTotal']} (esperado {total_esperado:.2f})")
    if not orc:
        sys.exit(1)

    s, aprovado = bill.chamar("POST", f"/api/orcamentos/{orc['id']}/aprovacao", {"aprovado": True})
    pagamento_id = aprovado["pagamento"]["id"] if s == 200 else None
    check("6. aprovação registrada e link de pagamento criado", s == 200, aprovado["pagamento"]["linkPagamento"] if s == 200 else aprovado)
    check("7. saga em AguardandoPagamento (OrcamentoAprovado)", bool(esperar(lambda: saga("AguardandoPagamento"))))
    if not pagamento_id:
        sys.exit(1)

    s, simulado = bill.chamar("POST", f"/dev/pagamentos/{pagamento_id}/simular?resultado=aprovado")
    check("8. pagamento simulado no gateway Fake", s == 202, simulado)
    check("9. PagamentoConfirmado → IniciarExecucao → saga EmExecucao", bool(esperar(lambda: saga("EmExecucao"))))
    e = esperar(lambda: execucao("AguardandoExecucao"))
    check("10. execution liberou o reparo com os serviços aprovados", bool(e), e and [x["descricao"] for x in e["servicos"]])

    exe.chamar("POST", f"/api/execucoes/{os_id}/reparo/iniciar")
    exe.chamar("POST", f"/api/execucoes/{os_id}/finalizar")
    check("11. ExecucaoFinalizada → saga Concluida", bool(esperar(lambda: saga("Concluida"))))

    _, pagamento = bill.chamar("GET", f"/api/pagamentos/{pagamento_id}")
    check("12. pagamento Aprovado no billing", isinstance(pagamento, dict) and pagamento.get("status") == "Aprovado")
    _, historico = os_api.chamar("GET", f"/api/ordens-servico/{os_id}/historico")
    estados = [h.get("statusNovo") for h in historico] if isinstance(historico, list) else []
    check("13. histórico da OS termina em Finalizada", bool(estados) and estados[-1] == "Finalizada", estados)

    print("\nTudo certo." if falhas == 0 else f"\n{falhas} falha(s).")
    sys.exit(1 if falhas else 0)


if __name__ == "__main__":
    main()
