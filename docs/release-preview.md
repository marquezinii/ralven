# Release preview, integridade e simulação

Este documento descreve a distribuição pública do **Ralven**. A versão
exata e suas mudanças ficam no [CHANGELOG](../CHANGELOG.md) e na página da
release correspondente; nenhuma delas é garantia de ganho de desempenho.

## Origem oficial

Baixe binários somente pela
[página oficial do Ralven](https://vemryx.com/Ralven/). Para cada release
`win-x64`, o bucket privado da Vemryx recebe os seguintes arquivos produzidos
pelo mesmo workflow:

- `Ralven-Setup-X.Y.Z-win-x64.exe`;
- `Ralven-Setup-X.Y.Z-win-x64.exe.sha256`;
- `Ralven-release-manifest-X.Y.Z.json`;
- `Ralven-Setup-latest-win-x64.exe`, como alias da release estável;
- `Ralven-win-x64.zip` e `Ralven-win-x64.zip.sha256` para instalação
  portátil;
- `Ralven-Runtime-win-x64.zip` e `Ralven-Runtime-win-x64.zip.sha256` para o
  atualizador transacional;
- manifestos assinados separados para o instalador e o runtime, nas releases estáveis.

Não use cópias hospedadas em encurtadores, mirrors, vídeos ou pacotes de
"FPS boost". O código-fonte correspondente deve estar disponível no mesmo tag
da release.

## Verificação SHA-256

Depois de baixar os dois arquivos para a mesma pasta, execute:

```powershell
$archive = Resolve-Path .\Ralven-win-x64.zip
$expected = ((Get-Content "$archive.sha256" -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()

if ($actual -ne $expected) {
    throw "SHA-256 divergente. Não execute este arquivo."
}

"SHA-256 confirmado: $actual"
```

O hash detecta corrupção e troca de arquivo. Como a release ainda não possui
assinatura de código pública, o hash sozinho não substitui identidade do
publicador: confira também o domínio `vemryx.com`, a versão e o código-fonte
associado.

## Atualização automática

Uma instalação atual usa `Ralven.Launcher.exe` e o layout versionado
`Runtime\versions\X.Y.Z`. Quando a opção de atualização automática está
ativada (padrão), cada abertura consulta o feed estável. O aplicativo só mostra
o aviso quando encontra uma versão semanticamente mais nova.

O feed e o pacote do atualizador são verificados com assinatura ECDSA, versão
mínima, tamanho e SHA-256. Depois do download, o ZIP também é validado por um
manifesto de hashes interno antes de ser extraído. A ativação troca somente o
ponteiro `Runtime\active.json`; a versão anterior permanece disponível. Se a
nova versão não abrir e confirmar saúde em até 45 segundos, o launcher restaura
automaticamente a versão anterior na próxima abertura.

O runtime não acumula um histórico ilimitado de binários. Em condições normais,
após uma atualização saudável ficam somente a versão ativa e seu predecessor
imediato, necessário durante a transição segura. Após rollback, a candidata que
falhou é removida. O cache de download mantém somente o pacote da atualização
atual. Pastas bloqueadas pelo sistema são tentadas de novo no próximo update;
pastas sem nome de versão reconhecido são ignoradas para evitar limpeza ampla.

O workflow só encerra uma release estável depois de consultar o feed público e
confirmar a versão, o hash do pacote e a assinatura recém-publicados. Se a
publicação do feed falhar depois de a GitHub Release de notas já existir, uma
reexecução republica os mesmos objetos versionados auditados antes de tornar os
aliases estáveis visíveis.

A promoção normal parte de um PR `release/vX.Y.Z` para `main`. O workflow fixa
o SHA, aguarda os checks obrigatórios, promove com proteção contra troca do
candidato, cria a tag, chama diretamente o build protegido e sincroniza a branch
de integração depois da verificação pública. O agente que preparou a versão pode
encerrar sua participação assim que essa execução for aceita: o GitHub registra
o resultado e não depende de consultas periódicas de uma sessão de IA.

O `workflow_dispatch` de release possui modos explícitos. `plan` executa apenas
a auditoria sem secrets, assinatura ou publicação; `build` produz um candidato
assinado sem distribuí-lo; `publish` conclui os efeitos externos. As versões
estáveis compartilham uma trava de publicação, pois todas escrevem os mesmos
aliases e feeds.

Para limitar armazenamento sem arriscar a versão pública, o workflow conserva
somente as 7 pastas de release SemVer mais recentes no R2 depois que a nova
publicação termina. Os aliases e manifestos em `stable/` são preservados. Não há
expiração por idade para binários completos: o lifecycle automático de 1 dia
serve apenas para abortar uploads multipart que não terminaram. A conta também
possui alerta de orçamento em US$ 5; ele envia aviso, mas não interrompe cobrança.

Instalações antigas ou execuções portáteis fora desse layout usam o manifesto
assinado do instalador completo. O botão manual em
Configurações força uma nova consulta e sempre informa se o app já está
atualizado ou se a consulta falhou.

## Build ainda não assinado

Os executáveis desta release são **unsigned** enquanto não houver certificado
Authenticode. Windows SmartScreen e produtos
antivírus podem, legitimamente, pedir confirmação ou bloquear um arquivo sem
reputação. Isso não deve ser contornado automaticamente.

O projeto nunca orienta o usuário a:

- desativar Defender, SmartScreen, firewall, UAC ou antivírus de terceiros;
- criar exclusão para a pasta ou executável;
- renomear, reempacotar ou ofuscar o binário para escapar de detecção;
- baixar uma cópia alternativa para evitar um alerta;
- executar um arquivo cujo hash diverge.

Se a política da máquina bloquear a release, a opção segura é não executá-la,
revisar/compilar o código-fonte ou aguardar uma release assinada. Um falso
positivo reproduzível pode ser relatado com produto, versão das assinaturas e
SHA-256, sem enviar arquivos pessoais a serviços externos.

## Atalho de simulação para desenvolvimento

O script `scripts/Install-DevelopmentShortcut.ps1`, disponível no checkout do
repositório e não no ZIP portátil, cria na Área de Trabalho um atalho para o
caminho estável do build `Release` dentro deste workspace. O atalho chama
`scripts/Start-DevelopmentApp.ps1`, que abre o executável WPF com
`--demo-synthetic`; a janela do PowerShell permanece oculta e não usa
`dotnet run`.

```powershell
# Compila Release e instala/atualiza o atalho.
.\scripts\Install-DevelopmentShortcut.ps1 -Build

# Se o build Release já existe, somente instala/atualiza o atalho.
.\scripts\Install-DevelopmentShortcut.ps1
```

O modo `--demo-synthetic` usa um diagnóstico FiveM Legacy/GTA V plausível e
simula o plano de otimização completo sem executar alterações, gravar opções
ou acessar o histórico real. Ele permite validar a interface de desenvolvimento
mesmo sem os jogos instalados. A versão pública não recebe esse argumento e
mantém a detecção real de FiveM Legacy e GTA V como pré-condição para as ações
compatíveis. A tela de relato de bug continua podendo acessar a rede **somente
depois de um clique explícito em Enviar**.

O `.lnk` não contém uma cópia congelada do aplicativo. Cada nova build Release
substitui o executável no mesmo destino, e o atalho passa a abrir esse build. Se
`bin/` for limpo ou o workspace for movido, execute novamente o script com
`-Build`.
