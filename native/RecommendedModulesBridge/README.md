# Recomendações independentes — núcleo experimental

Atualização de 2026-10-02: usuário confirmou a DLL r1 funcionando com English Lyrics e relatou incompatibilidade com KaiLen X. A integração r2 agora está incorporada ao conversor e ao mod de modelos: Resources/recommended, RecommendedModulesWriter e comando attach-recommended-modules. As conversões convencional e write-megamix-stage geram a tabela usando seus COS finais e somente as identidades emitidas. O plugin separado foi desativado. Pacotes completos r14 foram gerados sem o antigo PV DB; validação da integração no próprio mod aguarda próxima abertura do jogo. Metas e pendências estão em METAS.md.

Objetivo: distribuir recomendações junto da saída do XRomConverter sem editar o X Song Pack e sem fornecer outro mod_pv_db. Nenhum arquivo instalado foi alterado nesta etapa; a sobreposição que causava cenários ausentes continua desativada.

`recommendations.json` registra 40 substituições por ID existente de música, performer e personagem nativo. Cada entrada inclui COS interno e as identidades de módulos que o possuem. A fonte é o mapeamento aprovado r13, validado contra o catálogo de identidade r12. IDs de música não são criados ou renumerados.

`recommended_query.hpp` implementa o retorno condicional do COS recomendado. Destina-se somente à consulta de recomendação; não deve ser ligado à seleção manual ou à troca geral de costume. KAITO usa personagem nativo 6 e MEIKO 7. COS é zero-based: texto pv_costume=1003 corresponde ao retorno interno 1002, não ao ID do módulo.

`query_tests.cpp` compilou com avisos tratados como erros e passou: 40 substituições, toggle desligado, personagem extra, slots inválidos, músicas desconhecidas e preservação de todas as músicas abaixo de 800. Isso verifica o núcleo, não a integração em jogo.

Na referência ReDIVA, `pv_db_pv::get_performer_pv_costume` consulta recomendações, separadamente de `get_performer_costume`. O parser subtrai um de pv_costume. A consulta também trata dificuldade e performers pseudo, que precisam ser preservados pela integração. Fonte: https://github.com/korenkonder/ReDIVA, revisão c5d784a5d417f98b4511889a926943deff9ba35f, src/CRE/pv_db.cpp.

Atualização: foi localizado e implementado o adaptador da consulta no executável instalado. Há um pacote experimental instalável em pacotes/RecommendedModules-plugin-r1, descrito abaixo. A seleção manual e a apresentação dos cenários ainda precisam de confirmação em jogo.

Para integrar ao conversor, as entradas deverão ser regeneradas a partir das alocações finais de cada conversão, em vez de fixar permanentemente os IDs do catálogo atual. O gerador desta etapa está em work/prepare_recommended_bridge.py no workspace.

## DLL de teste concluída em 2026-10-02

`bridge.cpp` intercepta a consulta em RVA 0x4BCFC0. Sua desmontagem mostrou o array de cinco recomendações em performer+0x0C, fallback de dificuldade em +0x18 e tratamento de performers pseudo; o vetor começa em PV+0x98 e cada performer ocupa 0x44 bytes. O primeiro inteiro do PV é seu ID, confirmado também pelo caso especial nativo de PV 2. O patch verifica assinaturas e usa MinHook para preparar a chamada original. A função original é chamada primeiro; só entradas configuradas de vocal normal com personagem correspondente recebem um COS diferente. Performers pseudo preservam a resolução nativa.

`recommendations.tsv` é o arquivo de dados lido em runtime; modificar alocações não exige recompilar a DLL. O parser valida pares únicos, limites e enumeração nativa. Arquivo inválido desativa o plugin.

Passaram: política de 40 consultas, adaptação de layout nativo em memória sintética, fallback de pseudo/extras/entradas inválidas, instalação/remoção de um hook MinHook em memória de teste, carregamento da DLL pelo Windows e desativação em executável incompatível. A DLL exporta apenas Init, sem OnFrame/Present. Importa somente KERNEL32.dll e msvcrt.dll. Biblioteca MinHook usada: revisão 8af6b4acae5a9388fd742b56fa79ece89d96f823, licença incluída no pacote.

Instalado como mods/XRom02-RecommendedModules, com entrada acrescentada à prioridade existente e backup da configuração. Nenhum PV DB ou asset de modelo foi editado. ZIP: XRom02-RecommendedModules-plugin-r1.zip. Verificações e hashes: validacao/recommended-plugin-r1/verification.json.

Pendente: teste em jogo de Strangers/Silent Voice, Ai no Uta/Miku V3, cenários e seleção manual; depois integrar a geração da tabela à saída do conversor. Não tratar o pacote como release validada ainda.

O usuário relatou incompatibilidade de um PV DB modificado com a versão mais recente do X Song Pack. Isso restringe a conclusão anterior: a versão/compatibilidade do banco precisa ser considerada, não apenas sua sobreposição. O novo plugin preserva o banco instalado e permite avaliar a recomendação separadamente desse problema.
