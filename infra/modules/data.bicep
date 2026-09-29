@description('Azure region.')
param location string
@description('Resource name prefix.')
@minLength(3)
@maxLength(11)
param prefix string
@description('Deterministic uniqueness suffix.')
@minLength(8)
@maxLength(8)
param suffix string
@description('Common tags.')
param tags object
@description('New isolated VNet resource ID.')
param vnetId string
@description('Dedicated private endpoint subnet ID.')
param endpointSubnetId string
@description('Central workspace resource ID.')
param workspaceId string
@description('Admission managed identity object ID.')
param admissionPrincipalId string
@description('Worker managed identity object ID.')
param workerPrincipalId string

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  // checkov:skip=CKV_AZURE_36:Bypass None is intentionally stricter; runtime uses private endpoints, not trusted-service bypass.
  // checkov:skip=CKV_AZURE_43:Name is 2 + 3..11 lowercase prefix + 8 uniqueString characters; checked by provisioning validation.
  // checkov:skip=CKV_AZURE_206:Approved single-region ZRS demo; this check only accepts geo-replicated SKUs. No DR claim.
  name: 'st${prefix}${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_ZRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    allowCrossTenantReplication: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
      services: {
        blob: { enabled: true, keyType: 'Account' }
        file: { enabled: true, keyType: 'Account' }
        queue: { enabled: true, keyType: 'Account' }
        table: { enabled: true, keyType: 'Account' }
      }
    }
  }
}
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2025-01-01' = {
  parent: storage
  name: 'default'
  properties: {}
}
resource operations 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-01-01' = {
  parent: tableService
  name: 'operations'
  properties: {}
}
resource policies 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-01-01' = {
  parent: tableService
  name: 'policies'
  properties: {}
}
resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2025-01-01' = {
  parent: storage
  name: 'default'
  properties: {}
}
resource maintenance 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-01-01' = {
  parent: queueService
  name: 'maintenance'
  properties: {}
}
resource poison 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-01-01' = {
  parent: queueService
  name: 'maintenance-poison'
  properties: {}
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: 'kv-${prefix}-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'premium' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    enabledForDeployment: false
    enabledForTemplateDeployment: false
    enabledForDiskEncryption: false
    publicNetworkAccess: 'Disabled'
    accessPolicies: []
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

var endpointConfigs = [
  { name: 'table', zone: 'privatelink.table.${environment().suffixes.storage}', targetId: storage.id }
  { name: 'queue', zone: 'privatelink.queue.${environment().suffixes.storage}', targetId: storage.id }
  { name: 'vault', zone: 'privatelink.vaultcore.azure.net', targetId: vault.id }
]
resource zones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for item in endpointConfigs: {
  name: item.zone
  location: 'global'
  tags: tags
}]
resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (item, i) in endpointConfigs: {
  parent: zones[i]
  name: 'isolated-demo-vnet'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnetId }
  }
}]
resource endpoints 'Microsoft.Network/privateEndpoints@2024-07-01' = [for item in endpointConfigs: {
  name: 'pep-${prefix}-${item.name}'
  location: location
  tags: tags
  properties: {
    subnet: { id: endpointSubnetId }
    privateLinkServiceConnections: [{
      name: item.name
      properties: {
        privateLinkServiceId: item.targetId
        groupIds: [item.name]
      }
    }]
  }
}]
resource zoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-07-01' = [for (item, i) in endpointConfigs: {
  parent: endpoints[i]
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [{
      name: item.name
      properties: { privateDnsZoneId: zones[i].id }
    }]
  }
}]

var tableWriterRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
var tableReaderRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '76199698-9eea-4c19-bc75-cec21354c6b6')
var queueWriterRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var queueSenderRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a')

resource ledgerRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in [admissionPrincipalId, workerPrincipalId]: {
  scope: operations
  name: guid(operations.id, principalId, tableWriterRole)
  properties: { roleDefinitionId: tableWriterRole, principalId: principalId, principalType: 'ServicePrincipal' }
}]
resource policyRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in [admissionPrincipalId, workerPrincipalId]: {
  scope: policies
  name: guid(policies.id, principalId, tableReaderRole)
  properties: { roleDefinitionId: tableReaderRole, principalId: principalId, principalType: 'ServicePrincipal' }
}]
resource senderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: maintenance
  name: guid(maintenance.id, admissionPrincipalId, queueSenderRole)
  properties: { roleDefinitionId: queueSenderRole, principalId: admissionPrincipalId, principalType: 'ServicePrincipal' }
}
resource workerQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: maintenance
  name: guid(maintenance.id, workerPrincipalId, queueWriterRole)
  properties: { roleDefinitionId: queueWriterRole, principalId: workerPrincipalId, principalType: 'ServicePrincipal' }
}
resource workerPoisonRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: poison
  name: guid(poison.id, workerPrincipalId, queueWriterRole)
  properties: { roleDefinitionId: queueWriterRole, principalId: workerPrincipalId, principalType: 'ServicePrincipal' }
}
resource signingRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, prefix, 'github-sign-only')
  properties: {
    roleName: '${prefix} GitHub JWT sign only'
    description: 'Read key metadata and sign; no export, decrypt, import, administration or secret retrieval.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{
      actions: []
      notActions: []
      dataActions: [
        'Microsoft.KeyVault/vaults/keys/read'
        'Microsoft.KeyVault/vaults/keys/sign/action'
      ]
      notDataActions: []
    }]
  }
}
resource workerSigningRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, workerPrincipalId, signingRole.id)
  properties: {
    roleDefinitionId: signingRole.id
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource tableDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: tableService
  name: 'central-audit'
  properties: {
    workspaceId: workspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}
resource queueDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: queueService
  name: 'central-audit'
  properties: {
    workspaceId: workspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}
resource vaultDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: vault
  name: 'central-audit'
  properties: {
    workspaceId: workspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output storageAccountName string = storage.name
output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
output vaultId string = vault.id
output policiesTableId string = policies.id
