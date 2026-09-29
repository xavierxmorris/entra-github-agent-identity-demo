using './main.bicep'

// Illustrative values only. Deployment scripts pass real IDs from ignored local state.
param location = 'australiaeast'
param prefix = 'agentid3709'
param apiClientId = '11111111-1111-4111-8111-111111111111'
param delegatedClientId = '22222222-2222-4222-8222-222222222222'
param githubSubject = 'repo:example/identity-sandbox:environment:agent-demo'
param killSwitch = true
