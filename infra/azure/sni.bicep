// Turns on HTTPS for the custom host with its managed certificate (main.bicep binds the host and makes the certificate).

param appName string
param hostName string
param thumbprint string

resource app 'Microsoft.Web/sites@2023-12-01' existing = {
  name: appName
}

resource binding 'Microsoft.Web/sites/hostNameBindings@2023-12-01' = {
  parent: app
  name: hostName
  properties: {
    siteName: appName
    hostNameType: 'Verified'
    customHostNameDnsRecordType: 'CName'
    sslState: 'SniEnabled'
    thumbprint: thumbprint
  }
}
