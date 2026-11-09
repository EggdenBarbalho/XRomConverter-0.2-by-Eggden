# XRomRuntime r1 — Recommended Modules + QuirkyEyes

Patch para o mod XRom02-P4-Snow322-NewType atual. Copie o conteúdo dessa pasta por cima do mod de modelos. O config.toml ativa somente XRomRuntime.dll para estes recursos; outras DLLs do mod são preservadas.

Desative os plugins separados XRom02-QuirkyEyes, XRom02-QuirkyEyes-Diagnostic, XRom02-QuirkyEyes-Probe2 e XRom02-RecommendedModules. Reinicie o jogo. Não execute as antigas DLLs junto da unificada.

xrom-runtime.ini controla os recursos separadamente: enabled=1 ou enabled=0 em RecommendedModules e QuirkyEyes. Reinicie após mudar. O antigo quirky-eyes.ini não faz parte deste pacote; se copiado manualmente e desligado, também desliga o efeito.

Recomendações usam recommendations.tsv e somente IDs existentes de músicas. QuirkyEyes usa quirky-modules.tsv com COS finais, seis personagens principais e apenas módulos Quirky. Extra characters ficam excluídos. Não contém PV DB, não altera cenários ou Song Pack.

O conversor agora emite este runtime e as duas tabelas automaticamente a partir das alocações finais. Testes locais de remapeamento, preservação de DLL externa e repetição passaram. Os dois recursos já foram aprovados separadamente no jogo; esta combinação ainda precisa de teste em jogo. KaiLen X permanece incompatibilidade pendente.
