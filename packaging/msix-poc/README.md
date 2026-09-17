# Ralven MSIX POC

POC local e isolada para validar o empacotamento do payload desktop atual. Ela
não substitui o Inno Setup, não publica na Microsoft Store e não deve ser usada
como artefato de produção.

## Pré-requisitos

- Windows 10 2004 ou superior;
- SDK do Windows com `makeappx.exe` e `signtool.exe`;
- SDK .NET definido por `global.json`;
- certificado local de assinatura cujo subject corresponda ao `Publisher` do
  manifesto (`CN=Ralven MSIX POC`) para instalar fora da Store.

## Build

```powershell
.\packaging\msix-poc\Build-MsixPoc.ps1
```

O pacote não assinado é criado em `artifacts\msix-poc`. Para assinar com um
certificado de desenvolvimento já instalado em `Cert:\CurrentUser\My`:

```powershell
.\packaging\msix-poc\Build-MsixPoc.ps1 `
  -CertificateThumbprint '<THUMBPRINT>'
```

O certificado público também precisa estar em uma store confiável da máquina
de teste antes de `Add-AppxPackage`. Não confie nesse certificado em máquinas
de usuários; a Store substitui essa assinatura de desenvolvimento pela
assinatura Microsoft após a certificação.

## Escopo intencional

- reutiliza sem alteração o payload produzido por `Build-Portable.ps1`;
- ativa `Ralven.Launcher.exe` como aplicativo desktop `mediumIL`;
- inclui `runFullTrust` e a capability restrita `allowElevation`;
- mantém o Broker com seu manifesto `requireAdministrator`;
- registra um `StartupTask` desabilitado;
- executa o aplicativo com `--demo-synthetic`, evitando alterar estado real;
- preserva o Broker no payload e sua elevação sob demanda.

`--demo-synthetic` é instrumento da POC e deve sair do manifesto de produção.
