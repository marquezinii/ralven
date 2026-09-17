[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$temporaryRoot = [IO.Path]::GetFullPath((
    Join-Path $temporaryBase "ralven-automation-$([Guid]::NewGuid().ToString('N'))"))
if (-not $temporaryRoot.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The automation test directory escaped the operating-system temporary root.'
}

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw "Expected error '$MessagePattern', got: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected command to fail with '$MessagePattern'."
}

function New-PolicyFixture {
    param([Parameter(Mandatory)][string]$Destination)

    foreach ($directory in @(
        'src/Ralven.App/Resources',
        'src/Ralven.Contracts',
        'docs/codebase',
        'installer',
        'infra/cloudflare-worker/src',
        'infra/dashboard/assets',
        'website'
    )) {
        New-Item -ItemType Directory -Path (Join-Path $Destination $directory) -Force | Out-Null
    }

    foreach ($path in @(
        '.node-version',
        'Directory.Build.props',
        'global.json',
        'README.md',
        'docs/codebase/STACK.md',
        'docs/public-roadmap.json',
        'installer/Ralven.iss',
        'src/Ralven.App/Resources/Strings.resx',
        'src/Ralven.App/Resources/Strings.pt-BR.resx',
        'src/Ralven.App/Resources/Strings.es.resx',
        'src/Ralven.Contracts/BugCode.cs',
        'infra/cloudflare-worker/package.json',
        'infra/cloudflare-worker/package-lock.json',
        'infra/cloudflare-worker/src/bugCodes.js',
        'infra/dashboard/package.json',
        'infra/dashboard/package-lock.json',
        'infra/dashboard/assets/charts.js',
        'website/package.json',
        'website/package-lock.json'
    )) {
        Copy-Item -LiteralPath (Join-Path $workspace $path) -Destination (Join-Path $Destination $path)
    }
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    & (Join-Path $PSScriptRoot 'Test-RepositoryPolicy.ps1') -Workspace $workspace

    $releaseWorkflow = Get-Content -LiteralPath (Join-Path $workspace '.github/workflows/release.yml') -Raw
    $promotionWorkflow = Get-Content -LiteralPath (Join-Path $workspace '.github/workflows/promote-release.yml') -Raw
    $ciWorkflow = Get-Content -LiteralPath (Join-Path $workspace '.github/workflows/ci.yml') -Raw
    foreach ($requiredReleaseInvariant in @(
        'group: ralven-stable-release',
        "inputs.mode != 'plan'",
        "inputs.mode == 'publish'",
        'Build-Portable.ps1 -Runtime win-x64 -Configuration Release -Harden -SkipArchives'
    )) {
        if (-not $releaseWorkflow.Contains($requiredReleaseInvariant, [StringComparison]::Ordinal)) {
            throw "Release workflow invariant is missing: $requiredReleaseInvariant"
        }
    }
    if ($releaseWorkflow.Contains('inputs.publish', [StringComparison]::Ordinal)) {
        throw 'Release workflow must not expose the old signing-capable publish boolean as a dry-run.'
    }
    foreach ($requiredPromotionInvariant in @(
        'gh pr checks $env:RELEASE_PR',
        '--match-head-commit $env:CANDIDATE_SHA',
        'Test-ReleaseTagTarget.ps1',
        'uses: ./.github/workflows/release.yml',
        'Merge published main into dev/proxima-versao'
    )) {
        if (-not $promotionWorkflow.Contains($requiredPromotionInvariant, [StringComparison]::Ordinal)) {
            throw "Stable promotion workflow invariant is missing: $requiredPromotionInvariant"
        }
    }
    $pushTriggerEnd = $ciWorkflow.IndexOf('pull_request:', [StringComparison]::Ordinal)
    $pushTrigger = $ciWorkflow.Substring(0, $pushTriggerEnd)
    if ($pushTrigger.Contains('branches: [main', [StringComparison]::Ordinal)) {
        throw 'Full CI must not repeat after the already-validated release candidate is merged to main.'
    }

    $fixture = Join-Path $temporaryRoot 'policy'
    New-PolicyFixture -Destination $fixture

    [xml]$spanish = Get-Content -LiteralPath (Join-Path $fixture 'src/Ralven.App/Resources/Strings.es.resx') -Raw
    $spanish.root.RemoveChild($spanish.root.data[0]) | Out-Null
    $spanish.Save((Join-Path $fixture 'src/Ralven.App/Resources/Strings.es.resx'))
    Invoke-ExpectedFailure {
        & (Join-Path $PSScriptRoot 'Test-RepositoryPolicy.ps1') -Workspace $fixture
    } 'differs from Strings\.resx'

    Copy-Item -LiteralPath (Join-Path $workspace 'src/Ralven.App/Resources/Strings.es.resx') `
        -Destination (Join-Path $fixture 'src/Ralven.App/Resources/Strings.es.resx') -Force
    $workerCodesPath = Join-Path $fixture 'infra/cloudflare-worker/src/bugCodes.js'
    (Get-Content -LiteralPath $workerCodesPath -Raw).Replace("'APP_UI_RENDER', ", '') |
        Set-Content -LiteralPath $workerCodesPath -Encoding utf8
    Invoke-ExpectedFailure {
        & (Join-Path $PSScriptRoot 'Test-RepositoryPolicy.ps1') -Workspace $fixture
    } 'Worker bug-code allowlist'

    Copy-Item -LiteralPath (Join-Path $workspace 'infra/cloudflare-worker/src/bugCodes.js') `
        -Destination $workerCodesPath -Force
    $stackPath = Join-Path $fixture 'docs/codebase/STACK.md'
    $workerPackage = Get-Content -LiteralPath (Join-Path $workspace 'infra/cloudflare-worker/package.json') -Raw |
        ConvertFrom-Json
    $wranglerVersion = [regex]::Match([string]$workerPackage.devDependencies.wrangler, '\d+\.\d+').Value
    (Get-Content -LiteralPath $stackPath -Raw).Replace("Wrangler $wranglerVersion", 'Wrangler 0.0') |
        Set-Content -LiteralPath $stackPath -Encoding utf8
    Invoke-ExpectedFailure {
        & (Join-Path $PSScriptRoot 'Test-RepositoryPolicy.ps1') -Workspace $fixture
    } 'Wrangler major/minor version'

    Copy-Item -LiteralPath (Join-Path $workspace 'docs/codebase/STACK.md') `
        -Destination $stackPath -Force
    $roadmapPath = Join-Path $fixture 'docs/public-roadmap.json'
    $roadmap = Get-Content -LiteralPath $roadmapPath -Raw | ConvertFrom-Json -Depth 20
    $roadmap.items[1].id = $roadmap.items[0].id
    $roadmap | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $roadmapPath -Encoding utf8
    Invoke-ExpectedFailure {
        & (Join-Path $PSScriptRoot 'Test-RepositoryPolicy.ps1') -Workspace $fixture
    } 'Duplicate public roadmap item id'

    $prFixture = Join-Path $temporaryRoot 'pull-request'
    New-PolicyFixture -Destination $prFixture
    Push-Location $prFixture
    try {
        git init --quiet
        git config user.name 'Ralven Automation Test'
        git config user.email 'automation-test@localhost'
        git add .
        git commit --quiet -m 'test: initialize policy fixture'
        $baseSha = (git rev-parse HEAD).Trim()
        Set-Content -LiteralPath policy-fixture.txt -Value 'valid pull request fixture' -Encoding utf8
        git add policy-fixture.txt
        git commit --quiet -m 'test: add policy fixture'
        $headSha = (git rev-parse HEAD).Trim()
        & (Join-Path $PSScriptRoot 'Test-RepositoryPolicy.ps1') `
            -Workspace $prFixture `
            -PullRequestBaseRef 'dev/proxima-versao' `
            -PullRequestHeadRef 'chore/automation-test' `
            -PullRequestTitle 'ci: valida política do repositório' `
            -BaseSha $baseSha `
            -HeadSha $headSha
        Invoke-ExpectedFailure {
            & (Join-Path $PSScriptRoot 'Test-RepositoryPolicy.ps1') `
                -Workspace $prFixture `
                -PullRequestBaseRef 'dev/proxima-versao' `
                -PullRequestHeadRef 'chore/automation-test' `
                -PullRequestTitle 'Validate repository policy' `
                -BaseSha $baseSha `
                -HeadSha $headSha
        } 'Conventional Commits'
    }
    finally {
        Pop-Location
    }

    $notes = Join-Path $temporaryRoot 'release-notes.md'
    [xml]$props = Get-Content -LiteralPath (Join-Path $workspace 'Directory.Build.props') -Raw
    $currentVersion = [string]$props.Project.PropertyGroup.Version
    & (Join-Path $PSScriptRoot 'New-ReleaseNotes.ps1') -Version $currentVersion -OutputPath $notes
    $notesText = Get-Content -LiteralPath $notes -Raw
    if ($notesText -notmatch '^## ' -or $notesText -match '^### ') {
        throw 'Release note generation did not normalize the changelog headings.'
    }

    $multiSectionChangelog = Join-Path $temporaryRoot 'multiple-release-sections.md'
    @'
## [9.9.8] - 2026-01-01

### Adicionado

- Added.

### Melhorado

- Improved.

### Corrigido

- Fixed.

### Segurança

- Secured.

### Alterações técnicas

- Maintained.
'@ | Set-Content -LiteralPath $multiSectionChangelog -Encoding utf8
    & (Join-Path $PSScriptRoot 'New-ReleaseNotes.ps1') `
        -Version '9.9.8' -ChangelogPath $multiSectionChangelog -OutputPath $notes
    $multiSectionNotes = Get-Content -LiteralPath $notes -Raw
    foreach ($heading in @(
        '## ✨ Novidades',
        '## 🔧 Melhorias',
        '## 🐛 Correções',
        '## 🔒 Segurança',
        '## ⚙️ Alterações técnicas'
    )) {
        if (-not $multiSectionNotes.Contains($heading, [StringComparison]::Ordinal)) {
            throw "Release note generation omitted '$heading' from a multi-section changelog."
        }
    }

    $invalidChangelog = Join-Path $temporaryRoot 'CHANGELOG.md'
    "## [9.9.9] - 2026-01-01`n`n### Experimental`n`n- Unsupported." |
        Set-Content -LiteralPath $invalidChangelog -Encoding utf8
    Invoke-ExpectedFailure {
        & (Join-Path $PSScriptRoot 'New-ReleaseNotes.ps1') `
            -Version '9.9.9' -ChangelogPath $invalidChangelog -OutputPath $notes
    } 'unsupported section'

    $websiteScope = & (Join-Path $PSScriptRoot 'Get-CiScope.ps1') `
        -EventName pull_request -ChangedFiles @('website/app/page.tsx')
    if (-not $websiteScope.Website -or $websiteScope.Dotnet -or $websiteScope.Worker -or
        $websiteScope.Dashboard -or $websiteScope.Installer -or $websiteScope.Sbom) {
        throw 'Website-only changes must select only the website CI job.'
    }
    $versionScope = & (Join-Path $PSScriptRoot 'Get-CiScope.ps1') `
        -EventName pull_request -ChangedFiles @('Directory.Build.props')
    if (-not $versionScope.Dotnet -or -not $versionScope.Installer -or -not $versionScope.Sbom) {
        throw 'Product version changes must select .NET, installer and SBOM validation.'
    }
    $scheduledScope = & (Join-Path $PSScriptRoot 'Get-CiScope.ps1') -EventName schedule
    if (-not $scheduledScope.Dotnet -or -not $scheduledScope.Worker -or
        -not $scheduledScope.Dashboard -or -not $scheduledScope.Website -or
        $scheduledScope.Installer -or $scheduledScope.Sbom) {
        throw 'Scheduled CI must audit normal surfaces without rebuilding release artifacts.'
    }

    $gitFixture = Join-Path $temporaryRoot 'version'
    New-Item -ItemType Directory -Path $gitFixture | Out-Null
    Push-Location $gitFixture
    try {
        git init --quiet
        git config user.name 'Ralven Automation Test'
        git config user.email 'automation-test@localhost'
        git commit --quiet --allow-empty -m 'test: initialize fixture'
        git tag v1.1.99
        & (Join-Path $PSScriptRoot 'Test-PublicVersionProgression.ps1') -Version '1.1.100'
        Invoke-ExpectedFailure {
            & (Join-Path $PSScriptRoot 'Test-PublicVersionProgression.ps1') -Version '1.1.101'
        } 'Invalid public version'
    }
    finally {
        Pop-Location
    }

    $tagFixture = Join-Path $temporaryRoot 'release-tag'
    $tagRemote = Join-Path $temporaryRoot 'release-tag-remote.git'
    New-Item -ItemType Directory -Path $tagFixture | Out-Null
    git init --bare --quiet $tagRemote
    Push-Location $tagFixture
    try {
        git init --quiet
        git checkout --quiet -b main
        git config user.name 'Ralven Automation Test'
        git config user.email 'automation-test@localhost'
        '<Project><PropertyGroup><Version>1.1.100</Version></PropertyGroup></Project>' |
            Set-Content -LiteralPath ./Directory.Build.props -Encoding utf8
        git add Directory.Build.props
        git commit --quiet -m 'test: initialize release tag fixture'
        git remote add origin $tagRemote
        git push --quiet --set-upstream origin main

        & (Join-Path $PSScriptRoot 'Test-ReleaseTagTarget.ps1') -Version '1.1.100' -Workspace $tagFixture

        git checkout --quiet -b chore/unmerged
        'unmerged' | Set-Content -LiteralPath ./release.txt -Encoding utf8
        git add release.txt
        git commit --quiet -m 'test: unmerged release candidate'
        Invoke-ExpectedFailure {
            & (Join-Path $PSScriptRoot 'Test-ReleaseTagTarget.ps1') -Version '1.1.100' -Workspace $tagFixture
        } 'only after the release commit is current origin/main'
    }
    finally {
        Pop-Location
    }

    Write-Host 'Repository automation tests passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
