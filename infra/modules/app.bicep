@description('Azure region.')
param location string
@description('Resource name prefix.')
param prefix string
@description('Deterministic uniqueness suffix.')
param suffix string
@description('Common tags.')
param tags object
@description('Separately hosted process role.')
@allowed(['Admission', 'Worker'])
param mode string
@description('Only the managed identity belonging to this process.')
param identityId string
@description('Explicit managed identity client ID.')
param identityClientId string
@description('Dedicated, delegated App Service integration subnet.')
param subnetId string
@description('Private storage account name; not a connection string.')
param storageAccountName string
@description('Central diagnostic workspace ID.')
param workspaceId string
@description('Entra API client ID used as JWT audience.')
param apiClientId string
@description('Only the CI and delegated-client application IDs.')
param allowedClientIds array
@description('GitHub App ID; worker only.')
param githubAppId string = ''
@description('Versioned remote signing key URI; worker only.')
param keyVaultKeyUri string = ''
@description('Emergency runtime stop, independent of durable control.')
param killSwitch bool

var shortMode = toLower(mode)
var settings = concat([
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'Broker__Mode', value: mode }
  { name: 'Broker__TenantId', value: subscription().tenantId }
  { name: 'Broker__Audience', value: apiClientId }
  { name: 'Broker__ManagedIdentityClientId', value: identityClientId }
  { name: 'Broker__StorageAccountName', value: storageAccountName }
  { name: 'Broker__OperationsTableName', value: 'operations' }
  { name: 'Broker__PolicyTableName', value: 'policies' }
  { name: 'Broker__QueueName', value: 'maintenance' }
  { name: 'Broker__PoisonQueueName', value: 'maintenance-poison' }
  { name: 'Broker__KillSwitch', value: string(killSwitch) }
], mode == 'Worker' ? [
  { name: 'Broker__GitHubAppId', value: githubAppId }
  { name: 'Broker__KeyVaultKeyUri', value: keyVaultKeyUri }
] : [])

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  // checkov:skip=CKV_AZURE_225:Approved cost-bounded B1 demo, not zone-redundant production compute.
  name: 'asp-${prefix}-${shortMode}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
  properties: { reserved: true }
}
resource app 'Microsoft.Web/sites@2024-11-01' = {
  // checkov:skip=CKV_AZURE_17:OAuth bearer authentication with Entra is the selected protocol; no client-certificate flow.
  // checkov:skip=CKV_AZURE_212:Approved one-instance B1 per role; production HA is explicitly outside this demo.
  // checkov:skip=CKV_AZURE_222:Only Admission has intentional authenticated public ingress; Worker is Disabled by the mode expression.
  name: 'app-${prefix}-${shortMode}-${suffix}'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: mode == 'Admission' ? 'Enabled' : 'Disabled'
    virtualNetworkSubnetId: subnetId
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet MaintenanceBroker.dll'
      alwaysOn: true
      http20Enabled: true
      ftpsState: 'Disabled'
      remoteDebuggingEnabled: false
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      use32BitWorkerProcess: false
      healthCheckPath: '/healthz'
      scmIpSecurityRestrictionsUseMain: false
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
      ipSecurityRestrictionsDefaultAction: mode == 'Admission' ? 'Allow' : 'Deny'
      appSettings: settings
    }
  }
}
resource auth 'Microsoft.Web/sites/config@2022-09-01' = {
  // checkov:skip=CKV_AZURE_13:Legacy check expects authsettings.enabled; this resource uses authsettingsV2.platform.enabled.
  // checkov:skip=CKV_AZURE_63:Logging is configured in the sibling logs resource, not the authentication resource.
  // checkov:skip=CKV_AZURE_65:Detailed errors are intentionally off to avoid disclosure; structured diagnostics go to Log Analytics.
  // checkov:skip=CKV_AZURE_66:Failed-request tracing is intentionally off; audit and HTTP logging remain enabled.
  // checkov:skip=CKV_AZURE_80:Linux .NET 10, not Windows .NET Framework; linuxFxVersion is on the site.
  // checkov:skip=CKV_AZURE_88:No Azure Files needed; state is in private Table/Queue, not the application filesystem.
  // checkov:skip=CKV_AZURE_222:Inbound exposure is governed by the parent site, not authsettingsV2.
  parent: app
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true, runtimeVersion: '~1' }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
      excludedPaths: ['/healthz']
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: apiClientId
          openIdIssuer: '${environment().authentication.loginEndpoint}${subscription().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [apiClientId]
          defaultAuthorizationPolicy: { allowedApplications: allowedClientIds }
        }
      }
    }
    httpSettings: { requireHttps: true }
    login: { tokenStore: { enabled: false } }
  }
}
resource ftpPublishing 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: app
  name: 'ftp'
  properties: { allow: false }
}
resource scmPublishing 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: app
  name: 'scm'
  properties: { allow: false }
}
resource logging 'Microsoft.Web/sites/config@2022-09-01' = {
  // checkov:skip=CKV_AZURE_13:Authentication is configured in the sibling authsettingsV2 resource.
  // checkov:skip=CKV_AZURE_63:This logs resource enables httpLogs.fileSystem.enabled; the check expects the web-config shape.
  // checkov:skip=CKV_AZURE_65:Detailed errors are off to avoid disclosure; central diagnostics remain enabled.
  // checkov:skip=CKV_AZURE_66:Failed-request tracing is off to avoid credential/body capture; central HTTP/audit logs remain.
  // checkov:skip=CKV_AZURE_80:Linux .NET 10 is configured on the parent site; no .NET Framework.
  // checkov:skip=CKV_AZURE_88:No filesystem persistence needed; operations use private Table/Queue storage.
  // checkov:skip=CKV_AZURE_222:Inbound exposure is configured on the parent site.
  parent: app
  name: 'logs'
  properties: {
    applicationLogs: { fileSystem: { level: 'Information' } }
    httpLogs: { fileSystem: { enabled: true, retentionInDays: 3, retentionInMb: 35 } }
    detailedErrorMessages: { enabled: false }
    failedRequestsTracing: { enabled: false }
  }
}
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: app
  name: 'central-audit'
  properties: {
    workspaceId: workspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'AppServiceConsoleLogs', enabled: true }
      { category: 'AppServiceHTTPLogs', enabled: true }
      { category: 'AppServiceAuditLogs', enabled: true }
      { category: 'AppServicePlatformLogs', enabled: true }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output name string = app.name
output url string = 'https://${app.properties.defaultHostName}'
