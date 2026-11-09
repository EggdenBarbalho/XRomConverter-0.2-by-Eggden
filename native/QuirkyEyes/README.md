# QuirkyEyes � runtime r1 para teste em jogo

Runtime condicional implementado em runtime.cpp, com curvas separadas e toggle. Testes locais passaram; valida��o visual pendente. Pacote separado, sem PV DB. Consulte QUIRKY-EYES-RUNTIME-R1.md na raiz do projeto.

## Hist�rico experimental

# QuirkyEyes — núcleo experimental, sem DLL jogável

Atualização de 2026-10-02: segunda meta do mesmo projeto, após integrar as recomendações. probe2.cpp foi compilado e instalado como XRom02-QuirkyEyes-Probe2, exportando apenas Init. Captura uma região limitada do código do executável após carregamento para identificar as funções faciais protegidas no arquivo em disco; não usa OnFrame/Present, hooks, chamadas ao jogo ou escrita em memória. Teste no host incompatível não captura nada. A próxima abertura do jogo fornece o diagnóstico necessário para continuar. Não aplica efeito visual; os bancos híbridos continuam fora da instalação. METAS.md registra a sequência restante.

Estado em 2026-10-01: seleção condicional compilada e testada; bancos híbridos verificados fora do jogo; DLL de diagnóstico instalada separadamente. Ainda não há hooks de aplicação facial ou teste visual do efeito. Os assets da composição r13 foram preservados.

## Conteúdo

- `quirky-modules.json`: 67 registros Chaos da fonte, ligados aos módulos/COS atuais (Miku 24, Rin 9, Len 9, Luka 8, Kaito 11, Meiko 6).
- `quirky_policy.hpp`: filtro por performer, personagem, COS e módulo quando disponível; desativado fora do PV ou com toggle desligado. Estado independente para seis performers.
- `policy_tests.cpp`: classificação dos 319 módulos, duas Mikus com trajes diferentes, troca, desativação, reset e índices inválidos. Compilação C++17 com avisos tratados como erros e execução passaram.
- `eye-curves-original.json`: diferenças originais somente em ossos `eyelid`/`mabu`, em 394 motions. Não é um banco ROB convertido ou instalável.

## Evidência e limites

Os bancos padrão e `_uniq` dos seis personagens foram extraídos do data.psarc e comparados com o esqueleto bone_data.bon original: 570 motions, 95 por personagem. Há diferenças em face_reset, além das demais expressões. O vínculo entre Cloud=Chaos e a seleção desses bancos no executável original permanece uma hipótese a verificar. Snow Miku 2014, referência do usuário, aparece nesse grupo: módulo 284/COS 118 no destino.

O código público MMPlusMods, revisão 194f5495a17f316b300acf0f97875033b18afd56, expõe personagem e COS em rob_chara por instância. Os endereços dependem da versão e não foram verificados no executável instalado. motion_face_ids não basta para demonstrar como aplicar curvas e misturas.

## Trabalho restante

Identificar e verificar a avaliação facial por performer; adaptar apenas os canais dos olhos ao ROB compatível atual; preservar piscadas, olhos fechados, boca, dentes e sobrancelhas; compilar uma DLL com toggle e testar Quirky junto de um módulo normal no mesmo PV. Não substituir globalmente o ROB de um personagem.

As pastas encontradas na raiz são rom e rom_steam/rom. rom_switch está dentro de X Models - Complete Edition e contém apenas chritm_prop.farc, sem bancos ROB.

O relatório anterior QUIRKY-LOOK.md cobre somente a etapa inicial de dois personagens. Esta atualização e validacao/quirky-plugin/curves-comparison-six.json ampliam a investigação aos seis.

Fonte pública consultada: https://github.com/korenkonder/MMPlusMods

## Avanço: bancos híbridos fora do jogo

Foram gerados seis `offline-banks/quirky_*.bin` a partir dos bancos aprovados r13, utilizando o banco de nomes/IDs nativo de `rom/rob/mot_db.farc` e o esqueleto da composição r13. Cada personagem teve 103 motions faciais correspondentes, incluindo aliases antigos; `_OLD_CL` corresponde a `_CL`, preservando a escolha da versão de olhos fechados. As motions de boca não são alteradas. Animações sem correspondência permanecem intactas.

Somente bindings com nomes `eyelid`/`mabu` receberam curvas da fonte Quirky. Nenhum osso selecionado faltou no destino. A gravação e releitura dos seis bancos passaram na comparação exata das curvas dos olhos esperadas e de todos os canais fora desse filtro. Esses testes verificam integridade estrutural, não aparência ou proporções em jogo.

Os bancos retêm IDs existentes apenas para comparação offline. **Não copiar para rom/rob nem instalar como substituição global.** Ainda precisam de carregamento separado e seleção condicional por instância. O gerador está em `offline-builder`, e o relatório por motion está em `offline-banks/verification.json`.

A referência ReDIVA, revisão `c5d784a5d417f98b4511889a926943deff9ba35f`, descreve camadas independentes de rosto, boca, olhos e pálpebras: `load_face_motion`, `load_eyelid_motion` e `partial_motion_blend_mot::calc_fcurve`. Uma camada ativa de pálpebras mascara esses ossos na camada facial. Portanto, a integração deverá considerar as duas camadas e preservar sua mistura; substituir somente face_reset não cobre o fluxo completo. Essa referência ajuda a interpretar o motor, mas não confirma os endereços dessas funções no MegaMix+ instalado.

Referência adicional: https://github.com/korenkonder/ReDIVA

## Diagnóstico em jogo preparado e instalado

`QuirkyEyesDiagnostic.dll` foi compilada como DLL Windows x64, exportando Init/OnFrame do DIVA Mod Loader. Consulta os registros via ReadProcessMemory, sem chamar funções internas ou escrever na memória do jogo. Antes das consultas, verifica duas assinaturas exatas no executável em memória e deriva os endereços das tabelas a partir das instruções relativas. Desativa a consulta se o perfil não corresponder.

A desmontagem do executável instalado confirmou o getter em RVA 0x4EF3C0, sua referência à tabela em RVA 0x175D750 e stride 0x8118. O teste de presença em RVA 0x4EF4B0 confirmou a tabela em RVA 0x178DDE0 e stride 0xCC. Os offsets de personagem/COS seguem a estrutura pública e ainda precisam de confirmação pelos registros em jogo. O marcador de presença não é tratado como prova de que um PV está rodando.

`runtime_character.hpp` traduz a enumeração nativa para o catálogo: KAITO nativo 6 vira catálogo 4; MEIKO nativo 7 vira catálogo 5. Neru/Haku nativos 4/5 e demais extras são excluídos. Testes cobrem os 67 candidatos nessa tradução e a exclusão dos extras. A DLL passou no carregamento Windows e no teste de desativação em executável incompatível; suas dependências são apenas KERNEL32.dll e msvcrt.dll.

Instalação: `mods/XRom02-QuirkyEyes-Diagnostic` no jogo. Somente config.toml, README.md e a DLL foram copiados. A entrada foi acrescentada ao fim da prioridade existente porque o carregador de referência utiliza a lista explícita; a ordem anterior foi preservada. Backup da configuração e hashes estão em `validacao/quirky-plugin/runtime-diagnostic-r1`.

Teste pendente: Snow Miku 2014 junto de Miku padrão em Decorator, entrar no PV e voltar ao menu; opcionalmente General de KAITO/Border Break Operator de MEIKO. O log `quirky-diagnostic.jsonl` fica na pasta instalada. Não há mudança visual nesta etapa. O próximo passo depende desse registro para confirmar a identificação por performer antes da aplicação das curvas.

## Regressão relatada no primeiro teste

O usuário relatou cenários pretos nas músicas do X Song Pack. A ausência de mudança nos olhos era esperada para o diagnóstico. Ambos os PV DB instalados foram comparados byte a byte: X Song Pack corresponde ao original preservado; XRom corresponde à composição r13. A configuração raiz também só diferia do backup pela entrada do diagnóstico ao fim da prioridade.

O log contém 41 eventos, incluindo confirmação das assinaturas do perfil. Observou COS 118/Miku em 40 registros de slots, classificados como candidatos Quirky em 20; o marcador de presença alternou, portanto ainda não permite identificar sozinho a fase do jogo. Os campos de identidade têm evidência em execução, mas a classificação/presença ainda não está pronta para aplicar o efeito.

O diagnóstico foi desativado e a configuração raiz anterior restaurada byte a byte. O jogo estava aberto: é necessário encerrá-lo e abri-lo novamente para descarregar a DLL. A restauração visual dos cenários ainda precisa desse teste.

Hipótese a investigar: exportar OnFrame faz o DIVA Mod Loader instalar uma interceptação em IDXGISwapChain::Present, mesmo que nosso callback apenas leia memória. Isso pode interagir com a renderização dos mods existentes. Não há confirmação causal ainda. Não reativar esta versão antes de verificar o retorno dos cenários; o próximo diagnóstico deve evitar OnFrame/Present.

Log e relatório de reversão: `validacao/quirky-plugin/runtime-diagnostic-r1/regression-stages`.
