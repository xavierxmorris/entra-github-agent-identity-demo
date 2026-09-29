targetScope = 'resourceGroup'

@description('Deployment region. Keep all regional resources together.')
param location string = resourceGroup().location

@description('Lowercase alphanumeric name prefix; at most eleven characters.')
@minLength(3)
@maxLength(11)
param prefix string = 'agentid3709'

@description('Entra API application client ID, not its object ID or api:// URI.')
@minLength(36)
@maxLength(36)
param apiClientId string

@description('Entra public-client application ID for the delegated demo.')
@minLength(36)
@maxLength(36)
param delegatedClientId string

@description('Exact OIDC subject, for example repo:example/identity-sandbox:environment:agent-demo.')
param githubSubject string

@description('GitHub App numeric ID. Empty until the one-time registration is completed.')
param githubAppId string = ''

@description('Pinned versioned Key Vault key URI. No private key or secret is accepted.')
param keyVaultKeyUri string = ''

@description('Emergency runtime override. Keep true through bootstrap; policy control is a second independent gate.')
param killSwitch bool = true

var tags = {
  workload: 'entra-github-agent-identity-demo'
  environment: 'demo'
  dataClassification: 'synthetic'
}
var suffix = take(uniqueString(resourceGroup().id), 8)

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = [for mode in ['admission', 'worker', 'ci']: {
  name: 'id-${prefix}-${mode}'
  location: location
  tags: tags
}]

resource federation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: identities[2]
  name: 'protected-github-environment'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubSubject
    audiences: ['api://AzureADTokenExchange']
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'law-${prefix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    // Azure Monitor service endpoints are an explicit demo exception, not AMPLS.
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    location: location
    prefix: prefix
    tags: tags
  }
}

module data 'modules/data.bicep' = {
  name: 'data'
  params: {
    location: location
    prefix: prefix
    suffix: suffix
    tags: tags
    vnetId: network.outputs.vnetId
    endpointSubnetId: network.outputs.endpointSubnetId
    workspaceId: workspace.id
    admissionPrincipalId: identities[0].properties.principalId
    workerPrincipalId: identities[1].properties.principalId
  }
}

module admission 'modules/app.bicep' = {
  name: 'admission'
  params: {
    location: location
    prefix: prefix
    suffix: suffix
    tags: tags
    mode: 'Admission'
    identityId: identities[0].id
    identityClientId: identities[0].properties.clientId
    subnetId: network.outputs.admissionSubnetId
    storageAccountName: data.outputs.storageAccountName
    workspaceId: workspace.id
    apiClientId: apiClientId
    allowedClientIds: [identities[2].properties.clientId, delegatedClientId]
    killSwitch: killSwitch
  }
}

module worker 'modules/app.bicep' = {
  name: 'worker'
  params: {
    location: location
    prefix: prefix
    suffix: suffix
    tags: tags
    mode: 'Worker'
    identityId: identities[1].id
    identityClientId: identities[1].properties.clientId
    subnetId: network.outputs.workerSubnetId
    storageAccountName: data.outputs.storageAccountName
    workspaceId: workspace.id
    apiClientId: apiClientId
    allowedClientIds: [identities[2].properties.clientId, delegatedClientId]
    githubAppId: githubAppId
    keyVaultKeyUri: keyVaultKeyUri
    killSwitch: killSwitch
  }
}

output admissionUrl string = admission.outputs.url
output admissionAppName string = admission.outputs.name
output workerAppName string = worker.outputs.name
output admissionPrincipalId string = identities[0].properties.principalId
output workerPrincipalId string = identities[1].properties.principalId
output ciPrincipalId string = identities[2].properties.principalId
output ciClientId string = identities[2].properties.clientId
output ciIdentityName string = identities[2].name
output storageAccountName string = data.outputs.storageAccountName
output vaultName string = data.outputs.vaultName
output vaultUri string = data.outputs.vaultUri
output policiesTableId string = data.outputs.policiesTableId
output vaultId string = data.outputs.vaultId
output workspaceName string = workspace.name
