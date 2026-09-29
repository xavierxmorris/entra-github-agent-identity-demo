@description('Azure region.')
param location string
@description('Resource name prefix.')
param prefix string
@description('Common tags.')
param tags object

resource integrationNsgs 'Microsoft.Network/networkSecurityGroups@2024-07-01' = [for name in ['admission', 'worker']: {
  name: 'nsg-${prefix}-${name}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'allow-private-dependencies'
        properties: {
          priority: 100
          direction: 'Outbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '10.68.1.0/27'
        }
      }
      {
        name: 'deny-other-vnet-egress'
        properties: {
          priority: 200
          direction: 'Outbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'VirtualNetwork'
        }
      }
    ]
  }
}]

resource endpointNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = {
  name: 'nsg-${prefix}-endpoints'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'allow-app-https'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefixes: ['10.68.0.0/26', '10.68.0.64/26']
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'deny-other-inbound'
        properties: {
          priority: 200
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-07-01' = {
  name: 'vnet-${prefix}'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.68.0.0/16'] }
    subnets: [
      {
        name: 'snet-admission'
        properties: {
          addressPrefix: '10.68.0.0/26'
          networkSecurityGroup: { id: integrationNsgs[0].id }
          delegations: [{
            name: 'app-service'
            properties: { serviceName: 'Microsoft.Web/serverFarms' }
          }]
        }
      }
      {
        name: 'snet-worker'
        properties: {
          addressPrefix: '10.68.0.64/26'
          networkSecurityGroup: { id: integrationNsgs[1].id }
          delegations: [{
            name: 'app-service'
            properties: { serviceName: 'Microsoft.Web/serverFarms' }
          }]
        }
      }
      {
        name: 'snet-endpoints'
        properties: {
          addressPrefix: '10.68.1.0/27'
          networkSecurityGroup: { id: endpointNsg.id }
          privateEndpointNetworkPolicies: 'Enabled'
        }
      }
    ]
  }
}

output vnetId string = vnet.id
output admissionSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-admission')
output workerSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-worker')
output endpointSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-endpoints')
