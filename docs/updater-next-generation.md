# Arquitetura do atualizador de próxima geração

## Decisão reavaliada: custo zero e durabilidade

Não migrar para MSIX/App Installer no canal público gratuito. MSIX exige que
o certificado do pacote seja confiável em cada PC; com certificado gratuito
self-signed isso cria uma etapa administrativa manual. Um certificado de
produção confiável ou serviço de assinatura tem custo, portanto não atende ao
requisito de custo zero.

Substituir gradualmente Inno e o updater atual por uma distribuição própria
versionada, composta por um **Launcher/Recovery Agent** e diretórios de app
imutáveis. Ambos são self-contained .NET, sem serviço, driver, elevação,
dependência comercial ou instalação de certificado. A infraestrutura Vemryx hospeda
os pacotes e o Worker/Pages existente hospeda o feed e a observabilidade nos
planos gratuitos, sujeitos aos limites documentados desses provedores.

Não usar pinning de certificado TLS da Cloudflare ou do GitHub. Certificados
de borda podem ser rotacionados legitimamente. A cadeia de confiança terá dois
controles independentes: TLS padrão do Windows, com validação e revogação, e
assinatura de código/manifesto com chave pública fixa do Ralven.

## Cadeia de confiança

1. O instalador de transição ou download inicial instala somente o
   `Ralven.Launcher.exe` e uma versão conhecida em diretório por usuário.
2. O feed de release é um documento canônico, assinado com ECDSA P-256/SHA-256
   usando exclusivamente a criptografia nativa do .NET. O app e o
   Recovery Agent contêm somente a chave pública de produção e rejeitam feed,
   versão, URLs, hashes e assinatura inválidos.
3. O feed lista tamanho, SHA-256, versão, canal e URL HTTPS allowlisted. A chave privada fica fora do
   repositório, em segredo de release com acesso mínimo e rotação planejada.
4. TLS usa o validador nativo do Windows, SNI/hostname e revogação online. Não
   há callback permissivo de certificado, redirecionamento livre ou fallback
   HTTP.

### Proteção contra downgrade

O feed assinado também declara `minimumAllowedVersion` por canal. O Launcher
nunca ativa, baixa ou instala uma versão menor que a ativa ou menor que esse
piso, mesmo que alguém entregue um manifesto antigo, substitua a URL ou tente
abrir um pacote local. O estado local registra a maior versão já confirmada e
esse estado é protegido por DPAPI do usuário. O manifesto só é aceito quando a
versão ativa/confirmada também respeita `minimumAllowedVersion`; elevar o piso
acima da versão instalada exige o instalador manual, preservando a possibilidade
de rollback seguro.

Rollback não é um downgrade genérico: é uma transação limitada ao par
`previousVersion` registrado antes da ativação da candidata, dentro de uma
janela de recuperação curta e com journal/health receipt correspondente. O
Recovery Agent não aceita retornar a uma versão abaixo de
`minimumAllowedVersion`: a atualização automática é recusada antes do download
quando a versão ativa está abaixo do piso, pois nesse caso não existiria um
predecessor permitido para recuperação.

## Atualização e rollback verificáveis

O pacote novo é baixado sob `Updates/<versão>` e extraído em um diretório
temporário aleatório sob `staging`, recebe hash do bundle e
hash por arquivo, e só então é extraído para `versions/<version>`. A versão em
uso nunca é alterada. O Launcher troca um único ponteiro `active.json` por
`File.Replace`, depois de registrar uma transação local com versão anterior,
candidata, momento, nonce e estado. Esta é a atomicidade relevante:
uma inicialização vê a versão anterior inteira ou a nova inteira, nunca uma
árvore parcialmente copiada.

Após a primeira abertura, o app grava um *health receipt* com nonce da
transação somente depois de inicializar UI, configuração, broker compatível e
serviços essenciais.

Se não houver receipt dentro do prazo ou o novo processo encerrar,
o Recovery Agent restaura atomicamente o ponteiro para a versão anterior já
verificada no disco. O evento de rollback é persistido localmente antes de
qualquer telemetria; uma nova tentativa exige nova ação explícita no app.

Dados do usuário não são tratados como atômicos por MSIX. Cada migração de
dados deve ser versionada, journaling e reversível; a troca do ponteiro de
dados ocorre apenas após o health receipt. Migrações irreversíveis são
proibidas em uma atualização automática.

## Telemetria do updater

O Recovery Agent mantém log local detalhado, rotacionado e sem dados pessoais.
Para o Worker, envia apenas evento estruturado e limitado para `POST
/updater-events`: versão anterior/candidata, fase, código de erro, resultado e
ambiente. O servidor registra o horário de recebimento. Como diagnóstico
essencial, o envio depende da confirmação do aviso de privacidade vigente do
app, mas não do controle de relatórios opcionais; texto livre, caminhos, dumps
e logs completos nunca deixam o PC.
Eventos autorizados ficam em uma fila local limitada até o Worker responder
com sucesso; IDs únicos e inserção idempotente tornam o reenvio seguro.

O Worker valida o schema, limite e origem, persiste em tabela D1 própria e
oferece `GET /api/updater-events` exclusivamente sob sessão administrativa. O
dashboard ganha a aba **Bugs do updater**, sem expor essa URL em conteúdo
público. Os detalhes completos continuam acessíveis somente no log local que o
usuário escolhe compartilhar.

## Migração sem ruptura

O código, o empacotamento e o pipeline estão implementados. A próxima release
estável usa o Inno existente uma última vez como instalador de transição: ele
preserva `%LOCALAPPDATA%\Ralven`, atalhos e preferência de inicialização,
mas instala `Ralven.Launcher.exe` e a primeira versão imutável. A partir
daí, o botão de atualização usa exclusivamente o runtime ZIP assinado.

Antes dessa release, configure os ambientes GitHub abaixo. Chaves usadas por
Actions são **chaves online de CI**, não chaves offline: o ambiente
`release-signing` guarda a chave de broker em
`BROKER_INTEGRITY_SIGNING_PRIVATE_KEY`/`BROKER_INTEGRITY_SIGNING_PASSWORD`.
O par de update já implantado continua nos secrets legados
`RELEASE_SIGNING_PRIVATE_KEY`/`RALVEN_SIGNING_PASSWORD`, referenciados
somente pelo job isolado de assinatura para não romper clientes que confiam na
chave pública atual.
As respectivas chaves públicas ficam incorporadas em
`update-manifest-public-key.pem` e `broker-integrity-public-key.pem`; cada
segredo deve corresponder apenas ao seu arquivo público. O ambiente
`production` guarda `CLOUDFLARE_API_TOKEN` e `CLOUDFLARE_ACCOUNT_ID` e é o
único que pode publicar a release e o feed. A aprovação humana obrigatória
ocorre uma vez em `release-signing`; produção depende dessa assinatura aprovada
e mantém isolamento próprio de secrets sem uma segunda confirmação equivalente
do mesmo operador.

Os dois arquivos públicos têm escopo separado e o par ECDSA P-256 do broker é
distinto do par de update. Em rotações futuras, gere o novo par exclusivamente
fora do repositório, substitua `broker-integrity-public-key.pem` pela parte
pública e armazene a parte privada somente no ambiente `release-signing`.
Nunca copie a chave de update para o segredo do broker: isso preservaria a
mesma autoridade e invalidaria esta separação.

Uma raiz offline, se adotada, fica exclusivamente em HSM/cofre fora do GitHub
e assina ou autoriza as chaves online em procedimento manual. Ela nunca é
copiada para um secret, runner, artefato ou máquina de desenvolvimento. Um
certificado Authenticode é outra identidade: quando existir, deve ser mantido
em ambiente/provedor de assinatura próprio e não reutilizar nenhuma chave de
update ou de integridade. `installer/minimum-update-version.txt` é o piso
explícito do canal estável e só deve ser elevado após confirmar que a versão
indicada pode servir de predecessor seguro.

Inno continua apenas para instalação inicial, reparo e migração de instalações
legadas. Removê-lo desse papel somente depois de validação em Windows limpo e
evidência de adoção do launcher; ele não participa das atualizações seguintes.

## Critérios de aceite

- chave privada não existe no repositório, artefato ou máquina de usuário;
- bundle, feed, versão e hash inválidos não são instalados;
- uma versão abaixo da ativa ou de `minimumAllowedVersion` não é ativada;
- rollback só pode retornar ao predecessor registrado e nunca atravessa o
  piso de segurança assinado;
- perda de processo, rede ou energia não ativa pacote parcialmente estagiado;
- falha pós-atualização reativa a versão anterior e deixa evidência local;
- nenhuma migração automática torna dados incompatíveis com rollback;
- dashboard recebe somente eventos consentidos e sanitizados;
- instalação inicial, atualização, repair e rollback passam em Windows limpo.

## Compatibilidade com otimizações

As otimizações não dependem de uma pasta fixa do aplicativo: atuam em FiveM,
GTA V e configurações do Windows. Mesmo assim, o runtime novo deve preservar
sem alteração `%LOCALAPPDATA%\Ralven` para configurações, consentimento,
journals, rollback das otimizações e logs. Somente binários passam para
`Runtime\versions`; dados mutáveis ficam em `Data`, com contrato de migração
reversível. O broker é distribuído junto de cada versão e iniciado apenas pelo
Launcher da mesma versão, preservando seu contrato tipado e evitando mistura de
binários de versões diferentes.
