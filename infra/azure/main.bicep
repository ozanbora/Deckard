// Deckard's trade server on Azure, in one resource group (docs/deploy-azure.md):
// PostgreSQL Flexible Server, a Linux App Service running .NET 10, and a storage account whose file share is mounted
// into the app for the season data and icons, feedback screenshots and the app's releases.
// Secrets are parameters, given at deploy time (never in the repo):
//   az deployment group create -g <group> -f infra/azure/main.bicep -p name=deckard dbPassword=... signingKey=... \
//     battleNetClientId=... battleNetClientSecret=... hostName=trade.example.com

@description('Short lowercase name the resources are named after, e.g. deckard.')
@minLength(3)
@maxLength(16)
param name string

@description('Region: West Europe, as docs/privacy-policy.md says.')
param location string = 'westeurope'

@description('The public host the app and Battle.net reach the server at (a custom domain, else <name>-trade.azurewebsites.net).')
param hostName string = '${name}-trade.azurewebsites.net'

@description('PostgreSQL administrator login.')
param dbAdmin string = 'deckardadmin'

@secure()
@description('PostgreSQL administrator password.')
param dbPassword string

@secure()
@description('Signs the tokens players get at sign-in: a long random string (e.g. 64 random bytes, base64).')
param signingKey string

@secure()
@description('The Battle.net developer app (develop.battle.net) client id. Empty: sign-in answers that it is not set up.')
param battleNetClientId string = ''

@secure()
@description('The Battle.net developer app client secret.')
param battleNetClientSecret string = ''

@description('''Bind hostName to the app with a free App Service managed certificate. Turn on once the host's DNS has the CNAME
to <name>-trade.azurewebsites.net and the TXT asuid.<host> record (the customDomainVerificationId output).''')
param bindHostName bool = false

var storageName = toLower(replace('${name}trade${uniqueString(resourceGroup().id)}', '-', ''))
var shareName = 'deckard'
var mountPath = '/mnt/deckard'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take(storageName, 24)
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: shareName
  properties: { shareQuota: 20 } // GB: data and icons, screenshots for 90 days, a few releases
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2023-12-01-preview' = {
  name: '${name}-db'
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '16'
    administratorLogin: dbAdmin
    administratorLoginPassword: dbPassword
    storage: { storageSizeGB: 32 }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-12-01-preview' = {
  parent: postgres
  name: 'deckard'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

// 0.0.0.0 to 0.0.0.0 is Azure's "allow Azure services": the App Service reaches the database, the internet doesn't.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-12-01-preview' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${name}-plan'
  location: location
  sku: { name: 'B1', tier: 'Basic' }
  kind: 'linux'
  properties: { reserved: true }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: '${name}-trade'
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet Deckard.Trade.Api.dll' // without it the container may not find the app and fails its startup probe
      alwaysOn: true // the daily retention cleanup and the GitHub feedback filer run inside the server
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      azureStorageAccounts: {
        deckard: {
          type: 'AzureFiles'
          accountName: storage.name
          shareName: shareName
          mountPath: mountPath
          accessKey: storage.listKeys().keys[0].value
        }
      }
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        // App Service ends TLS and forwards: without this the server sees http and the proxy's address (the HTTPS
        // redirect, the sign-in callbacks and the per-IP sign-in limit depend on it).
        { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        { name: 'Trade__SigningKey', value: signingKey }
        { name: 'Trade__BattleNet__ClientId', value: battleNetClientId }
        { name: 'Trade__BattleNet__ClientSecret', value: battleNetClientSecret }
        { name: 'Trade__BattleNet__RedirectUri', value: 'https://${hostName}/auth/battlenet/callback' }
        { name: 'Trade__DataRoot', value: '${mountPath}/data' }
        { name: 'Trade__FeedbackRoot', value: '${mountPath}/feedback' }
        { name: 'Trade__UpdatesRoot', value: '${mountPath}/updates' }
      ]
      connectionStrings: [
        {
          name: 'Trade'
          type: 'PostgreSQL'
          connectionString: 'Host=${postgres.properties.fullyQualifiedDomainName};Database=deckard;Username=${dbAdmin};Password=${dbPassword};SSL Mode=Require;Maximum Pool Size=15'
        }
      ]
    }
  }
  dependsOn: [ database, allowAzure, share ]
}

// The managed certificate needs the host bound first; the binding then takes the certificate in a module, since one
// file can't declare the same binding twice.
resource binding 'Microsoft.Web/sites/hostNameBindings@2023-12-01' = if (bindHostName) {
  parent: app
  name: hostName
  properties: {
    siteName: app.name
    hostNameType: 'Verified'
    customHostNameDnsRecordType: 'CName'
    sslState: 'Disabled'
  }
}

resource certificate 'Microsoft.Web/certificates@2023-12-01' = if (bindHostName) {
  name: hostName
  location: location
  properties: {
    serverFarmId: plan.id
    canonicalName: hostName
  }
  dependsOn: [ binding ]
}

module sni 'sni.bicep' = if (bindHostName) {
  name: 'sni'
  params: {
    appName: app.name
    hostName: hostName
    thumbprint: certificate!.properties.thumbprint
  }
}

output appUrl string = 'https://${app.properties.defaultHostName}'
output battleNetRedirectUri string = 'https://${hostName}/auth/battlenet/callback'
output storageAccount string = storage.name
output customDomainVerificationId string = app.properties.customDomainVerificationId
