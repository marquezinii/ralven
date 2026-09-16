# Instalador, atualização e publicação

O instalador oficial do Ralven é um executável Inno Setup moderno para
Windows 10 versão 2004 (build 19041) ou mais recente e Windows 11, em sistemas
compatíveis com binários x64. As duas plataformas são suportadas como ambientes
de primeira classe; recursos visuais nativos sem suporte no Windows 10 usam o
fallback equivalente documentado pelo aplicativo.

O roteiro de aceite em uma máquina Windows 10 real está em
[`windows10-validation.md`](windows10-validation.md).
Em instalações novas, ele instala por usuário em `{autopf}\Ralven`; por padrão, isso corresponde
à pasta de programas local do usuário e não exige UAC.

## Dependências e funcionamento offline

O aplicativo WPF e o broker administrativo são publicados como `win-x64`
**self-contained**, em múltiplos arquivos e sem trimming. O runtime do .NET
Desktop, o CoreCLR e as bibliotecas nativas ficam dentro do instalador. O PC do
usuário não precisa ter o .NET instalado e a instalação não baixa scripts,
runtimes ou pacotes da internet.

Essa escolha é intencional: elimina falhas de proxy/rede no primeiro uso e evita
executar conteúdo remoto que possa mudar depois de a release ser criada. O
broker continua separado e pede elevação somente quando uma ação protegida do
Windows realmente for executada.

## Experiência do instalador

- português do Brasil, inglês, espanhol e francês, escolhidos pela interface do Windows;
- entrada limpa com a marca do Ralven, explicação breve e uma ação principal;
- tema moderno que acompanha o modo claro/escuro do sistema, com ícone oficial,
  tipografia Segoe UI, arte lateral e dimensões confortáveis para Windows 10 e 11;
- licença e informações completas convertidas para RTF no build. Títulos,
  listas, destaques, caracteres Unicode e links são preservados, e o texto
  extraído do RTF é comparado semanticamente com a fonte antes da compilação;
- instalação apresentada em quatro etapas reais: preparação, cópia do pacote,
  criação dos atalhos/preferências e finalização. O percentual exibido vem do
  progresso nativo do Inno, sem atraso ou valor simulado;
- detalhes técnicos recolhidos por padrão e exibidos, sem trocar de página,
  somente quando a pessoa escolhe **Mostrar detalhes**;
- etapas distinguem pendente, em andamento e concluído; falhas interrompem o
  fluxo e permanecem evidentes nos diálogos nativos do instalador;
- atalhos do menu Iniciar e desinstalação completa, com rótulos localizados;
- identidade de shell estável `Ralven.Ralven` nos atalhos, mantendo nome e ícone
  oficiais independentemente do caminho de instalação;
- atalhos da Área de Trabalho e de inicialização com o Windows habilitados por
  padrão em instalações novas, ambos podendo ser desmarcados pela pessoa e
  alterados depois em Configurações;
- página final lembra que atualizações futuras vêm pelo app, com confirmação;
- compressão `lzma2/ultra` no pacote offline self-contained;
- upgrade no mesmo diretório por meio de um `AppId` estável;
- Windows Restart Manager para solicitar o fechamento seguro do app durante um
  upgrade, sem encerramento forçado nem reinicialização automática;
- logs padrões do Inno Setup para diagnóstico (pasta temporária quando ativos).

Configurações, journals, logs, backups e downloads de atualização ficam fora da
pasta de instalação, em `%LOCALAPPDATA%\Ralven` durante a ponte. Na desinstalação
interativa, a pessoa escolhe se deseja preservar ou remover esses dados. A
opção padrão é preservar; uma desinstalação silenciosa também preserva os dados
para nunca apagar histórico ou backup sem confirmação visível.

Essa identidade é o mecanismo Win32 suportado para o shell, mas não existe API
pública do Microsoft PC Manager para cadastrar caminhos em **Limpeza Profunda >
Outros itens do aplicativo**. A associação eventual depende do scanner interno
da Microsoft; o Ralven não cria registros ou pacotes artificiais para forçar a
exibição. A classificação e a limpeza manual estão em
[`docs/cache.md`](cache.md).

## Build local reproduzível

```powershell
.\scripts\Build-Installer.ps1 -Version 1.0.0 -Harden

$installer = Resolve-Path .\artifacts\installer\Ralven-Setup-1.0.0-win-x64.exe
.\scripts\Test-Installer.ps1 `
  -InstallerPath $installer `
  -PublishDirectory .\artifacts\Ralven-win-x64 `
  -ExpectedVersion 1.0.0
```

Para uma simulação de release, `-Harden` é obrigatório; sem esse switch o build
é deliberadamente limpo e serve apenas ao desenvolvimento. O script primeiro
executa a verificação de segurança e o publish self-contained protegido,
gera e valida os documentos RTF, depois compila o instalador, gera SHA-256 e um
manifesto de release. Se o Inno
Setup 7.0.2 x64 não estiver instalado, o build baixa a release imutável oficial para
um cache dentro de `artifacts/.tools`, exige o SHA-256 fixado no script e valida
a assinatura Authenticode de `Pyrsys B.V.` antes de executar o compilador.

O teste instala silenciosamente em uma pasta temporária sob `artifacts`, confere
byte a byte todo o payload, valida o padrão desktop-on/startup-on e o opt-out
individual de cada task, o handoff `/AUTOUPDATE=yes`, a preservação de
dados em `%LOCALAPPDATA%\Ralven` no uninstall silencioso, executa a
desinstalação e confirma a remoção. Ele se recusa a rodar se encontrar uma
instalação real ou uma entrada de inicialização existente. Somente para uma
validação local explicitamente autorizada, `-AllowExistingInstallation` libera
essa trava; como o AppId é o mesmo, esse modo pode alterar o registro da
instalação local e não deve ser usado como gate de release.

Em CI (`GITHUB_ACTIONS`/`CI`), `Build-Installer.ps1` recusa worktree suja para
não publicar manifesto com `sourceDirty=true`. Localmente o build continua
permitido; use `-AllowDirtySource` só se precisar forçar o mesmo em CI.

## Contrato de atualização

O Inno Setup é somente o instalador inicial e a ponte para instalações legadas.
Atalhos apontam para `Ralven.Launcher.exe`; cada versão do app fica
imutável em `Runtime\versions\<versão>`. O aplicativo consulta somente o
manifesto estável assinado do Worker e nunca atualiza sem confirmação.

Depois do clique do usuário, o atualizador:

1. exibe a página oficial das alterações da release, quando disponível;
2. valida contrato fechado, assinatura ECDSA P-256, chave pública incorporada,
   SemVer, `minimumAllowedVersion`, URL Vemryx allowlisted, tamanho e SHA-256;
3. baixa somente `Ralven-Runtime-win-x64.zip` via TLS 1.2/1.3, valida
   revogação e cada redirecionamento, limita tamanho e grava com nome parcial;
4. valida novamente o ZIP e o `SHA256SUMS.txt`; arquivos extras, ausentes,
   duplicados, alterados, caminhos externos e pacotes de extração excessiva são
   rejeitados;
5. move a árvore completa para `Runtime\versions\<versão>`, registra journal e
   troca `active.json` atomicamente;
6. fecha o app anterior e chama o launcher. A candidata precisa gravar um
   health receipt com nonce em até 45 segundos;
7. sem receipt, o launcher restaura somente o predecessor registrado. Uma
   versão saudável avança o piso anti-downgrade protegido por DPAPI;
8. depois da confirmação saudável, preserva somente a versão ativa e o seu
   predecessor imediato; depois de rollback, remove a candidata que falhou.
   Downloads de versões anteriores também são removidos ao baixar a próxima.
   Se antivírus ou outro processo mantiver uma pasta em uso, o update continua
   seguro e a limpeza é tentada novamente na atualização seguinte;
9. nunca desativa SmartScreen, Defender, UAC ou antivírus de terceiros.

Na primeira abertura após uma reinstalação manual sobre a instalação existente,
o launcher aplica a mesma retenção depois de reconciliar o piso anti-downgrade.

Falhas de manifesto, download, staging, ativação e saúde preservam a versão
anterior. Logs detalhados ficam locais; eventos essenciais sanitizados chegam à
área administrativa somente após a confirmação do aviso de privacidade vigente.

## Publicação no GitHub

O workflow `.github/workflows/promote-release.yml` recebe o número de um PR
`release/vX.Y.Z` para `main`, fixa seu SHA, aguarda os checks obrigatórios,
promove exatamente esse commit, valida `origin/main`, cria a tag e chama
diretamente `.github/workflows/release.yml`. O workflow de release também pode
ser iniciado por uma tag estável exata `vX.Y.Z` ou por `workflow_dispatch`
controlado. Antes de qualquer segredo, ele
confirma que a tag identifica o `origin/main` atual e compila/testa somente o
código limpo, sem produzir candidato publicável. Um job separado, protegido
pelo ambiente `release-signing`, recompila, ofusca e valida diretamente o
runtime protegido antes de assinar os manifestos de update e broker com chaves
online distintas. Mappings de diagnóstico saem desse ambiente apenas como bundle
AES-256-GCM autenticado, preservado no R2 e nunca anexado à release pública. A
aprovação humana ocorre uma vez, em `release-signing`; o ambiente `production`
continua isolando as credenciais de deploy e só é alcançado depois do job
protegido. O modo manual `plan` valida sem acessar secrets, assinar ou publicar;
`build` gera o candidato assinado sem distribuição e `publish` conclui a
publicação.

Antes de criar a release, o workflow repete build, testes, instalação e
desinstalação; gera checksums; assina e verifica os manifestos do runtime e do
broker; aplica o schema D1; implanta e verifica o Worker, o dashboard e o feed;
gera as notas a partir do `CHANGELOG.md`; e produz uma atestação de proveniência
do instalador. O bot oficial observa as releases e publica o anúncio com sua
própria identidade; o workflow não usa mais um webhook separado. O binário permanece sem assinatura de código até
existir um certificado Authenticode. SHA-256 e atestação aumentam a
transparência, mas não substituem reputação ou uma assinatura pública.

### Sequência de versões públicas

A versão segue SemVer conforme `AI_RULES.md`: correção compatível usa patch,
nova capacidade compatível usa minor e mudança incompatível usa major. O script
`scripts/Test-PublicVersionProgression.ps1` recusa regressões e saltos que não
sejam uma progressão SemVer pública válida.

Fontes oficiais usadas no desenho:

- [Inno Setup: recursos e suporte de Windows](https://jrsoftware.org/isinfo.php)
- [Inno Setup: modo não administrativo](https://jrsoftware.org/ishelp/topic_admininstallmode.htm)
- [Inno Setup: AppId e upgrades](https://jrsoftware.org/ishelp/topic_setup_appid.htm)
- [Inno Setup: tema moderno e dinâmico](https://jrsoftware.org/ishelp/topic_setup_wizardstyle.htm)
- [Inno Setup: Restart Manager](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)
- [Inno Setup: verificação dos downloads oficiais](https://jrsoftware.org/isdl-verify.php)
- [GitHub: releases em workflows](https://docs.github.com/actions/using-workflows/events-that-trigger-workflows#push)

## Procedimento de release

1. Crie `release/vX.Y.Z` do SHA escolhido de `dev/proxima-versao`; atualize
   `Directory.Build.props`, `CHANGELOG.md` e os demais contratos de versão.
2. Execute as validações locais aplicáveis e abra o PR dessa branch para
   `main`; não continue alterando o candidato depois do dispatch.
3. Inicie `promote-release.yml` com o número do PR e aprove uma vez o ambiente
   `release-signing` quando a origem validada for apresentada.
4. A automação aguarda o gate, promove, cria a tag, compila, assina, publica,
   verifica os feeds e sincroniza `dev/proxima-versao`. Em falha, diagnostique
   a etapa registrada; não recrie versão nem contorne validações.

### Anúncios no Discord

O bot oficial consulta as releases e o roadmap público e publica os avisos com
a própria identidade no Discord. Não use webhooks de release: eles publicariam
como uma integração separada e duplicariam as mensagens do bot.

O push manual da tag ainda prepara automaticamente a release como fallback,
mas o fluxo normal usa a promoção coordenada e uma única confirmação humana
antes do acesso às chaves. A página pública de download é `https://vemryx.com/Ralven/`, gratuita e sem login para
visitantes. O botão da página usa `Ralven-Setup-latest-win-x64.exe`; a mesma
release também publica o instalador versionado e o alias
`Ralven-Setup-latest-win-x64.exe` no bucket privado da Vemryx.
