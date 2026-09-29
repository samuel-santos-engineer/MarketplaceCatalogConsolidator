[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ValidationResourceGroup,

    [ValidateNotNullOrEmpty()]
    [string]$Region = 'westcentralus'
)

$ErrorActionPreference = 'Stop'

function Invoke-AzureCliJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: az $($Arguments -join ' ')"
    }

    return $output | ConvertFrom-Json
}

$account = Invoke-AzureCliJson @('account', 'show', '--output', 'json')
if ($account.state -ne 'Enabled') {
    throw "The active Azure subscription is not enabled."
}

$locations = Invoke-AzureCliJson @('appservice', 'list-locations', '--sku', 'F1', '--linux-workers-enabled', '--output', 'json')
$regionDisplayName = 'West Central US'
if (-not ($locations | Where-Object { $_.name -eq $regionDisplayName })) {
    throw "Azure does not list $regionDisplayName for Linux workers on F1 in the active subscription."
}

$subscriptionId = $account.id
$accessToken = & az account get-access-token --query accessToken --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw 'An in-memory Azure Resource Manager access token could not be obtained.'
}

$planName = 'validate-mcc-f1-linux'
$appName = 'validate-mcc-f1-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$template = @{
    '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#'
    contentVersion = '1.0.0.0'
    resources      = @(
        @{
            type       = 'Microsoft.Web/serverfarms'
            apiVersion = '2024-04-01'
            name       = $planName
            location   = $Region
            kind       = 'linux'
            sku        = @{ name = 'F1'; tier = 'Free'; size = 'F1'; capacity = 1 }
            properties = @{ reserved = $true }
        },
        @{
            type       = 'Microsoft.Web/sites'
            apiVersion = '2024-04-01'
            name       = $appName
            location   = $Region
            kind       = 'app,linux,container'
            dependsOn  = @("[resourceId('Microsoft.Web/serverfarms', '$planName')]")
            properties = @{
                serverFarmId = "[resourceId('Microsoft.Web/serverfarms', '$planName')]"
                httpsOnly    = $true
                siteConfig   = @{
                    linuxFxVersion = 'DOCKER|mcr.microsoft.com/azuredocs/aci-helloworld:latest'
                    alwaysOn       = $false
                }
            }
        }
    )
}
$request = @{ properties = @{ mode = 'Incremental'; template = $template } } | ConvertTo-Json -Depth 20
$headers = @{ Authorization = "Bearer $accessToken" }
$uri = "https://management.azure.com/subscriptions/$subscriptionId/resourcegroups/$ValidationResourceGroup/providers/Microsoft.Resources/deployments/validate-mcc-f1-linux/validate?api-version=2022-09-01"
$validation = Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/json' -Body $request
if ($validation.properties.provisioningState -ne 'Succeeded') {
    throw 'ARM did not validate the F1 Linux custom-container deployment.'
}

[pscustomobject]@{
    SubscriptionName            = $account.name
    SignedInAs                  = $account.user.name
    SubscriptionState           = $account.state
    Region                      = $Region
    Sku                         = 'F1'
    LinuxWorkersListed          = $true
    CustomContainerValidation   = $validation.properties.provisioningState
    ResourcesCreatedByThisCheck = 0
} | Format-List
