[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,89}$')]
    [string]$ResourceGroup = 'rg-marketplace-catalog-wcus',

    [ValidatePattern('^[A-Za-z0-9-]{1,60}$')]
    [string]$PlanName = 'plan-marketplace-catalog-f1-wcus',

    [ValidatePattern('^[a-z0-9][a-z0-9-]{1,58}[a-z0-9]$')]
    [string]$AppName = 'marketplace-catalog-consolidator-ss-wcus',

    [ValidatePattern('^[a-z0-9][a-z0-9._-]*$')]
    [string]$GitHubOwner = 'samuel-santos-engineer',

    [ValidateNotNullOrEmpty()]
    [string]$ValidationResourceGroup = 'rg-aiq-r112-wp03-wcus-5ec325382770'
)

$ErrorActionPreference = 'Stop'
$region = 'westcentralus'
$packageName = 'marketplace-catalog-consolidator'
$imageRepository = "ghcr.io/$GitHubOwner/$packageName"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repositoryRoot

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }

    return $output | ConvertFrom-Json
}

function Invoke-ArmRequest {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'PUT')][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [string]$Body
    )

    $headers = @{ Authorization = "Bearer $script:armToken" }
    if ($Method -eq 'GET') {
        return Invoke-RestMethod -Method Get -Uri $Uri -Headers $headers
    }

    return Invoke-RestMethod -Method Put -Uri $Uri -Headers $headers -ContentType 'application/json' -Body $Body
}

function Get-ApiKeyText {
    $secureKey = Read-Host 'Enter the production API key (input is hidden)' -AsSecureString
    if ($secureKey.Length -lt 32) {
        throw 'The production API key must contain at least 32 characters.'
    }

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $secureKey.Dispose()
    }
}

function Assert-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' is not available on PATH."
    }
}

foreach ($command in @('az', 'gh', 'git', 'curl.exe')) {
    Assert-CommandAvailable $command
}

$dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
if (-not $dockerCommand) {
    $defaultDockerPath = 'C:\Program Files\Docker\Docker\resources\bin'
    if (Test-Path (Join-Path $defaultDockerPath 'docker.exe')) {
        $env:PATH = "$defaultDockerPath;$env:PATH"
        $dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
    }
}
if (-not $dockerCommand) {
    throw 'Docker CLI is not available on PATH or in its standard Windows installation directory.'
}

$branch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'main') {
    throw 'Deployment is allowed only from the local main branch after merge.'
}

& git fetch --quiet origin main
if ($LASTEXITCODE -ne 0) {
    throw 'Could not refresh origin/main.'
}

$head = (& git rev-parse HEAD).Trim()
$remoteMain = (& git rev-parse origin/main).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $remoteMain -or $head -notmatch '^[0-9a-f]{40}$') {
    throw 'Local main must exactly match origin/main before deployment.'
}

$status = & git status --porcelain
if ($LASTEXITCODE -ne 0 -or $status) {
    throw 'The working tree must be clean before deployment.'
}

$account = Invoke-AzJson @('account', 'show', '--output', 'json')
if ($account.state -ne 'Enabled' -or -not $account.isDefault) {
    throw 'The active Azure subscription must be enabled and the selected default subscription.'
}

[pscustomobject]@{
    RepositoryCommit = $head
    AzureIdentity    = $account.user.name
    Subscription     = $account.name
    SubscriptionId   = $account.id
    Region            = $region
    PlanSku           = 'F1'
} | Format-List

$confirmation = Read-Host "Type DEPLOY-F1-$AppName to authorize creating only the documented F1 App Service resources"
if ($confirmation -cne "DEPLOY-F1-$AppName") {
    throw 'Deployment confirmation did not match; no cloud resources were created.'
}

& (Join-Path $PSScriptRoot 'Test-AppServiceF1Feasibility.ps1') -ValidationResourceGroup $ValidationResourceGroup -Region $region
if ($LASTEXITCODE -ne 0) {
    throw 'The read-only F1 Linux custom-container feasibility gate failed.'
}

$locations = Invoke-AzJson @('appservice', 'list-locations', '--sku', 'F1', '--linux-workers-enabled', '--output', 'json')
if (-not ($locations | Where-Object { $_.name -eq 'West Central US' })) {
    throw 'West Central US is not listed for Linux F1 in the active subscription.'
}

$subscriptionId = $account.id
$appNameRequest = @{ name = $AppName; type = 'Microsoft.Web/sites' } | ConvertTo-Json -Compress
$armToken = & az account get-access-token --resource https://management.azure.com/ --query accessToken --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($armToken)) {
    throw 'Could not obtain an in-memory Azure Resource Manager token for the read-only name check.'
}
try {
    $availabilityUri = "https://management.azure.com/subscriptions/$subscriptionId/providers/Microsoft.Web/checknameavailability?api-version=2024-04-01"
    $availability = Invoke-RestMethod -Method Post -Uri $availabilityUri -Headers @{ Authorization = "Bearer $armToken" } -ContentType 'application/json' -Body $appNameRequest
}
finally {
    $armToken = $null
}
if (-not $availability.nameAvailable) {
    throw "App Service name '$AppName' is unavailable; no image or Azure resource was created."
}

$groupExists = (& az group exists --name $ResourceGroup).Trim()
if ($LASTEXITCODE -ne 0 -or $groupExists -ne 'false') {
    throw "Resource group '$ResourceGroup' already exists or could not be checked; refusing to modify existing resources."
}

$existingAppCount = Invoke-AzJson @('webapp', 'list', '--query', "[?name=='$AppName'] | length(@)", '--output', 'json')
if ([int]$existingAppCount -ne 0) {
    throw "Web app '$AppName' already exists; refusing to modify existing resources."
}

$dockerInfo = & $dockerCommand.Source info --format '{{.ServerVersion}}' 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dockerInfo)) {
    throw 'The local Docker daemon is unavailable.'
}

$apiKey = Get-ApiKeyText
$imageTag = $head
$mutableImage = "${imageRepository}:$imageTag"

Write-Host 'Docker will prompt interactively for GHCR authentication. Do not paste credentials into this script or a shell command.'
& $dockerCommand.Source login ghcr.io
if ($LASTEXITCODE -ne 0) {
    throw 'Interactive GHCR login failed; no image was built or pushed.'
}

& $dockerCommand.Source build --pull --file Dockerfile --tag $mutableImage .
if ($LASTEXITCODE -ne 0) {
    throw 'Docker build failed; no image was pushed.'
}

& $dockerCommand.Source push $mutableImage
if ($LASTEXITCODE -ne 0) {
    throw 'GHCR push failed; no Azure resources were created.'
}

$digest = (& $dockerCommand.Source buildx imagetools inspect $mutableImage --format '{{json .Manifest.Digest}}').Trim('"')
if ($LASTEXITCODE -ne 0 -or $digest -notmatch '^sha256:[a-f0-9]{64}$') {
    throw 'A valid immutable image digest could not be read; no Azure resources were created.'
}
$immutableImage = "$imageRepository@$digest"

$ghAccount = & gh api user --jq .login
if ($LASTEXITCODE -ne 0 -or $ghAccount.Trim() -ine $GitHubOwner) {
    throw 'GitHub CLI must be authenticated as the image repository owner before package visibility can be changed.'
}

& gh api --method PATCH "/user/packages/container/$packageName" -f visibility=public | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Could not make the GHCR package public. Set package visibility to Public in GitHub and rerun; Azure resources were not created.'
}

$dockerConfig = Join-Path ([IO.Path]::GetTempPath()) ('mcc-anon-ghcr-' + [guid]::NewGuid().ToString('N'))
$hadDockerConfig = Test-Path Env:DOCKER_CONFIG
$previousDockerConfig = $env:DOCKER_CONFIG
try {
    New-Item -ItemType Directory -Path $dockerConfig | Out-Null
    $env:DOCKER_CONFIG = $dockerConfig
    & $dockerCommand.Source manifest inspect $immutableImage | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Anonymous GHCR manifest access failed; no Azure resources were created.'
    }
}
finally {
    if ($hadDockerConfig) { $env:DOCKER_CONFIG = $previousDockerConfig }
    else { Remove-Item Env:DOCKER_CONFIG -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $dockerConfig -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Image repository: $imageRepository"
Write-Host "Full commit SHA tag: $imageTag"
Write-Host "Immutable image digest: $digest"
Write-Host 'Anonymous GHCR manifest access: PASS'

$armToken = & az account get-access-token --resource https://management.azure.com/ --query accessToken --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($armToken)) {
    throw 'Could not obtain an in-memory Azure Resource Manager token; no Azure resources were created.'
}

try {
    & az group create --name $ResourceGroup --location $region --output none
    if ($LASTEXITCODE -ne 0) { throw 'Resource group creation failed.' }

    & az appservice plan create --name $PlanName --resource-group $ResourceGroup --location $region --sku F1 --is-linux --output none
    if ($LASTEXITCODE -ne 0) { throw 'Linux F1 App Service plan creation failed.' }

    & az webapp create --name $AppName --resource-group $ResourceGroup --plan $PlanName --container-image-name $immutableImage --https-only true --output none
    if ($LASTEXITCODE -ne 0) { throw 'App Service web app creation failed.' }

    $settingsUri = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$AppName/config/appsettings?api-version=2022-03-01"
    $settingsBody = @{
        properties = @{
            ASPNETCORE_ENVIRONMENT                    = 'Production'
            ASPNETCORE_HTTP_PORTS                     = '8080'
            WEBSITES_PORT                             = '8080'
            WEBSITES_ENABLE_APP_SERVICE_STORAGE       = 'true'
            Catalog__StorageRoot                     = '/home/data'
            Security__ApiKey                         = $apiKey
        }
    } | ConvertTo-Json -Depth 5 -Compress
    $null = Invoke-ArmRequest -Method PUT -Uri $settingsUri -Body $settingsBody

    $siteConfigUri = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$AppName/config/web?api-version=2022-03-01"
    $siteConfigBody = @{ properties = @{ linuxFxVersion = "DOCKER|$immutableImage" } } | ConvertTo-Json -Depth 5 -Compress
    $null = Invoke-ArmRequest -Method PUT -Uri $siteConfigUri -Body $siteConfigBody

    $site = Invoke-AzJson @('webapp', 'show', '--name', $AppName, '--resource-group', $ResourceGroup, '--query', '{httpsOnly:httpsOnly,state:state}', '--output', 'json')
    if (-not $site.httpsOnly) { throw 'HTTPS-only configuration did not read back as enabled.' }

    $configuredImage = (& az resource show --resource-group $ResourceGroup --resource-type Microsoft.Web/sites/config --name "$AppName/web" --query properties.linuxFxVersion --output tsv).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read back the configured App Service image.' }
    if ($configuredImage -ne "DOCKER|$immutableImage") { throw 'The configured App Service image is not the expected immutable digest.' }

    $settingsReadback = Invoke-AzJson @('webapp', 'config', 'appsettings', 'list', '--name', $AppName, '--resource-group', $ResourceGroup, '--output', 'json')
    $settingMap = @{}
    foreach ($setting in $settingsReadback) { $settingMap[$setting.name] = $setting.value }
    if ($settingMap['Catalog__StorageRoot'] -ne '/home/data' -or $settingMap['WEBSITES_ENABLE_APP_SERVICE_STORAGE'] -ne 'true') {
        throw 'App Service persistent storage settings did not read back as expected.'
    }
    if ($settingMap.ContainsKey('Development__UsePlaceholderApiKey')) {
        throw 'The development placeholder API-key setting must not be enabled in Azure.'
    }

    $baseUrl = "https://$AppName.azurewebsites.net"
    $ready = $false
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        try {
            $response = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/ready" -TimeoutSec 15
            if ($response.status -eq 'ready') { $ready = $true; break }
        }
        catch { }
        Start-Sleep -Seconds 10
    }
    if (-not $ready) { throw 'The live application did not become ready within 10 minutes.' }

    $root = Invoke-WebRequest -Uri "$baseUrl/" -UseBasicParsing
    if ($root.StatusCode -ne 200 -or $root.Content -notmatch 'Marketplace Catalog') { throw 'The static catalog UI check failed.' }
    $swagger = Invoke-WebRequest -Uri "$baseUrl/swagger/" -UseBasicParsing
    if ($swagger.StatusCode -ne 200 -or $swagger.Content -notmatch 'Swagger') { throw 'The Swagger UI check failed.' }
    $health = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/health"
    if ($health.status -ne 'healthy') { throw 'The health check failed.' }

    $idempotencyKey = [guid]::NewGuid().ToString('D')
    $multipart = [System.Net.Http.MultipartFormDataContent]::new()
    try {
        $fileBytes = [IO.File]::ReadAllBytes((Join-Path $repositoryRoot 'artifacts/ProductEntry.json'))
        $fileContent = [System.Net.Http.ByteArrayContent]::new($fileBytes)
        $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/json')
        $multipart.Add($fileContent, 'file', 'ProductEntry.json')
        $uploadClient = [System.Net.Http.HttpClient]::new()
        try {
            $uploadClient.DefaultRequestHeaders.Add('X-Api-Key', $apiKey)
            $uploadClient.DefaultRequestHeaders.Add('Idempotency-Key', $idempotencyKey)
            $uploadResponse = $uploadClient.PostAsync("$baseUrl/api/v1/uploads", $multipart).GetAwaiter().GetResult()
            if (-not $uploadResponse.IsSuccessStatusCode) { throw "Validation upload failed with HTTP $([int]$uploadResponse.StatusCode)." }
            $uploadResult = $uploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        }
        finally {
            $uploadClient.Dispose()
        }
    }
    finally {
        $multipart.Dispose()
    }

    $uploadId = [string]$uploadResult.uploadId
    if ($uploadId -notmatch '^[0-9a-fA-F-]{36}$') { throw 'The validation upload did not return a valid upload ID.' }

    $reportReady = $false
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        $status = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/uploads/$uploadId/status"
        if ($status.upload.reportAvailable -eq $true) { $reportReady = $true; break }
        Start-Sleep -Seconds 5
    }
    if (-not $reportReady) { throw 'The uploaded fixture did not produce a final report.' }

    $report = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/uploads/$uploadId/report"
    if ($report.uploadId -ne $uploadId -or $report.summary.received -ne ($report.summary.approved + $report.summary.cleaned + $report.summary.rejected)) {
        throw 'The initial upload report failed identity or summary validation.'
    }

    & az webapp restart --name $AppName --resource-group $ResourceGroup --output none
    if ($LASTEXITCODE -ne 0) { throw 'The controlled App Service restart failed.' }

    $persisted = $false
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        try {
            $status = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/uploads/$uploadId/status" -TimeoutSec 15
            if ($status.upload.reportAvailable -eq $true) { $persisted = $true; break }
        }
        catch { }
        Start-Sleep -Seconds 10
    }
    if (-not $persisted) { throw 'The accepted upload did not survive the App Service restart.' }

    $persistedReport = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/uploads/$uploadId/report"
    if ($persistedReport.uploadId -ne $uploadId) { throw 'The report did not survive the App Service restart.' }

    [pscustomobject]@{
        Region                 = $region
        PlanSku                = 'F1'
        AppName                = $AppName
        PublicUrl              = $baseUrl
        ImageRepository        = $imageRepository
        ImageTag               = $imageTag
        ImageDigest            = $digest
        AnonymousPull          = 'PASS'
        HttpsOnly              = $site.httpsOnly
        StorageRoot            = '/home/data'
        Health                 = $health.status
        Readiness              = 'ready'
        ValidationUploadId     = $uploadId
        PersistenceAfterRestart = 'PASS'
    } | Format-List
}
finally {
    $apiKey = $null
    $armToken = $null
}