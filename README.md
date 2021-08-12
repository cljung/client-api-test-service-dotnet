# client-api-test-service-dotnet
Azure AD Verifiable Credentials ASP.Net Core 3.1  sample that uses the new private preview VC Client APIs.
This sample is updated to work with version v0.3 which introduces authorization and the use of an access token when calling the API. Please read the section on [Adding Authorization](https://github.com/cljung/client-api-test-service-dotnet/#adding-authorization) to see what additions you need to do.

## Two modes of operations
This sample can work in two ways:
- As a standalone WebApp with it's own web UI that let's you issue and verify DID Verifiable Credentials.
- As an API service that works in combination with Azure AD B2C in order to use VCs as a way to authenticate to B2C.

## VC Client API, what is that?

Initially, Microsoft provided a node.js SDK for building Verifiable Credentials applications. Going forward, it will be replaced by an API, since an API makes implementation easier and also is programming language agnostic. Instead of understanding the various functions in the SDK, in the programming language you use, you only have to understand how to format JSON structures for issuing or verifying VCs and call the VC Client API. 

![API Overview](media/api-overview.png)

## Issuance

### Issuance JSON structure

To call the VC Client API to start the issuance process, the DotNet API creates a JSON structure like below. 

```JSON
{
  "authority": "did:ion: ...of the Issuer",
  "includeQRCode": true,
  "registration": {
    "clientName": "the verifier's client name"
  },
  "callback": {
    "url": "https://contoso.com/api/issuer/issuanceCallback",
    "state": "you pass your state here to correlate it when you get the callback",
    "headers": {
        "keyname": "any value you want in your callback"
    }
  },
  "issuance": {
    "type": "your credentialType",
    "manifest": "https://beta.did.msidentity.com/v1.0/3c32ed40-8a10-465b-8ba4-0b1e86882668/verifiableCredential/contracts/VerifiedCredentialExpert",
    "pin": {
      "value": "012345",
      "length": 6
    },
    "claims": {
      "mySpecialClaimOne": "mySpecialValueOne",
      "mySpecialClaimTwo": "mySpecialValueTwo"
    }
  }
}
```

- **authority** - is the DID identifier for your registered Verifiable Credential i portal.azure.com.
- **includeQRCode** - If you want the VC Client API to return a `data:image/png;base64` string of the QR code to present in the browser. If you select `false`, you must create the QR code yourself (which is not difficult).
- **registration.clientName** - name of your app which will be shown in the Microsoft Authentictor
- **callback.url** - a callback endpoint in your DotNet API. The VC Client API will call this endpoint when the issuance is completed.
- **callback.state** - A state value you provide so you can correlate this request when you get callback confirmation
- **callback.headers** - Any HTTP Header values that you would like the VC Clint API to pass back in the callbacks. Here you could set your own API key, for instance
- **issuance.type** - the name of your credentialType. Usually matches the last part of the manifest url
- **issuance.manifest** - url of your manifest for your VC. This comes from your defined Verifiable Credential in portal.azure.com
- **issuance.pin** - If you want to require a pin code in the Microsoft Authenticator for this issuance request. This can be useful if it is a self issuing situation where there is no possibility of asking the user to prove their identity via a login. If you don't want to use the pin functionality, you should not have the pin section in the JSON structure. The appsettings.PinCode.json contains a settings for issuing with pin code.
- **issuance.claims** - optional, extra claims you want to include in the VC.

In the response message from the VC Client API, it will include it's own callback url, which means that once the Microsoft Authenticator has scanned the QR code, it will contact the VC Client API directly and not your DotNet API. The DotNet API will get confirmation via the callback.

```json
{
    "requestId": "799f23ea-524a-45af-99ad-cf8e5018814e",
    "url": "openid://vc?request_uri=https://dev.did.msidentity.com/v1.0/abc/verifiablecredentials/request/178319f7-20be-4945-80fb-7d52d47ae82e",
    "expiry": 1622227690,
    "qrCode": "data:image/png;base64,iVBORw0KGgoA<SNIP>"
}
```

### Issuance Callback

In your callback endpoint, you will get a callback with the below message when the QR code is scanned.

```JSON
{"code":"request_retrieved","requestId":"9463da82-e397-45b6-a7a2-2c4223b9fdd0", "state": "...what you passed as the state value..."}
```

## Verification

### Verification JSON structure

To call the VC Client API to start the verification process, the DotNet API creates a JSON structure like below. Since the WebApp asks the user to present a VC, the request is also called `presentation request`.

```JSON
{
  "authority": "did:ion: did-of-the-Verifier",
  "includeQRCode": true,
  "registration": {
    "clientName": "the verifier's client name",
    "logoUrl": "https://test-relyingparty.azurewebsites.net/images/did_logo.png"
  },
  "callback": {
    "url": "https://contoso.com/api/verifier/presentationCallback",
    "state": "you pass your state here to correlate it when you get the callback",
    "headers": {
        "keyname": "any value you want in your callback"
    }
  },
  "presentation": {
    "includeReceipt": true,
    "requestedCredentials": [
      {
        "type": "your credentialType",
        "manifest": "https://portableidentitycards.azure-api.net/dev/536279f6-15cc-45f2-be2d-61e352b51eef/portableIdentities/contracts/MyCredentialTypeName",
        "purpose": "the purpose why the verifier asks for a VC",
        "trustedIssuers": [ "did:ion: ...of the Issuer" ]
      }
    ]
  }
}
```

Much of the data is the same in this JSON structure, but some differences needs explaining.

- **authority** vs **trustedIssuers** - The Verifier and the Issuer may be two different entities. For example, the Verifier might be a online service, like a car rental service, while the DID it is asking for is the issuing entity for drivers licenses. Note that `trustedIssuers` is a collection of DIDs, which means you can ask for multiple VCs from the user
- **presentation** - required for a Verification request. Note that `issuance` and `presentation` are mutually exclusive. You can't send both.
- **requestedCredentials** - please also note that the `requestedCredentials` is a collection too, which means you can ask to create a presentation request that contains multiple DIDs.
- **includeReceipt** - if set to true, the `presentation_verified` callback will contain the `receipt` element.

### Verification Callback

In your callback endpoint, you will get a callback with the below message when the QR code is scanned.

When the QR code is scanned, you get a short callback like this.
```JSON
{"code":"request_retrieved","requestId":"c18d8035-3fc8-4c27-a5db-9801e6232569", "state": "...what you passed as the state value..."}
```

Once the VC is verified, you get a second, more complete, callback which contains all the details on what whas presented by the user.

```JSON
{
    "code":"presentation_verified",
    "requestId":"c18d8035-3fc8-4c27-a5db-9801e6232569",
    "state": "...what you passed as the state value...",
    "subject": "did:ion: ... of the VC holder...",
    "issuers": [
      {
          "type": [ "VerifiableCredential", "your credentialType" ],
          "claims": {
            "displayName":"Alice Contoso",
            "sub":"...",
            "tid":"...",
            "username":"alice@contoso.com",
            "lastName":"Contoso",
            "firstName":"alice"
          }
      }
    ],
    "receipt":{
        "id_token": "...JWT Token of VC..."
        }
    }
}
```
Some notable attributes in the message:
- **claims** - parsed claims from the VC
- **receipt.id_token** - the DID of the presentation

## Adding Authorization 
The v0.3 version of the VC Client API requires that you pass an access token for authorization. This sample now uses MSAL to aquire an access token via client_credentials.
The access token is needed because the VC Client API needs to access your Azure Key Vault to get your signing keys.
You therefor need to create a new App Registration in your Azure AD tenant (the tenant that is protecting your Azure subscription where your Azure Key Vault is deployed). 
This App also needs to have API Permissions granted to VC Client API, because this API is going to be the one accessing your Azure Key Vault. 

### VC Client API Service Principle
First you need to create a Sercice Principle for the API `Verifiable Credential Request Service`, which is the Microsoft service that will access your Key Vault.
You do this via the following Powershell command

```Powershell
Connect-AzureAD -TenantId <your-tenantid-guid>
New-AzureADServicePrincipal -AppId "bbb94529-53a3-4be5-a069-7eaf2712b826" -DisplayName "Verifiable Credential Request Service" 
```

### App Registration for Client Credentials
Your app needs a way to get an access token and this is done via the client credentials flow. You can register a Web app, accept the defaults, and set the redirect_uri `https://localhost`.
The important thing is to add an `API Permission` for API `Verifiable Credential Request Service` and permission `VerifiableCredential.Create.All`.

### Update your Access Policy for Azure Key Vault
The VC Client API needs to have access to your Azure Key Vault. You need to add a new `Access Policy` in your Azure Key Vault for the API `Verifiable Credential Request Service` with "Get" and "Sign" for Key Permissions and "Get" for Secret Permissions.

### Update appsettings.json
In the appsettings.json file, you add the tenant id (guid) for the `TenantID`, the AppID for the `ClientId` and the secret for the `ClientSecret`.  

```JSON
    "TenantId": "",
    "ClientId": "",
    "ClientSecret": "",
```
## Running the sample

### Standalone
To run the sample standalone, just clone the repository, compile & run it. It's callback endpoint must be publically reachable, and for that reason, use `ngrok` as a reverse proxy to read your app.

```Powershell
git clone https://github.com/cljung/client-api-test-service-dotnet.git
cd client-api-test-service-dotnet
dotnet build "client-api-test-service-dotnet.csproj" -c Debug -o .\bin\Debug\netcoreapp3.1
dotnet run
```

You can specify a which credentials you should issue and verify by specifying the payload files on the command line. Read more on how to author these files below.
```Powershell
dotnet run /IssuanceRequestConfigFile=%cd%\requests\issuance_request_cljungdemob2c.json /PresentationRequestConfigFile=%cd%\requests\presentation_request_cljungdemob2c.json
```

Then, open a separate command prompt and run the following command

```Powershell
ngrok http 5002
```

Grab, the url in the ngrok output (like `https://96a139d4199b.ngrok.io`) and Browse to it.

### Docker build

To run it locally with Docker
```
docker build -t client-api-test-service-dotnet:v1.0 .
docker run --rm -it -p 5002:80 -e IssuanceRequestConfigFile=./requests/issuance_request_config_v2.json -e PresentationRequestConfigFile=./requests/presentation_request_config_v2.json client-api-test-service-dotnet:v1.0
```

Then, open a separate command prompt and run the following command

```Powershell
ngrok http 5002
```

Grab, the url in the ngrok output (like `https://96a139d4199b.ngrok.io`) and Browse to it.

### Author the json payload files

There are a few samples of json files in the `requests` folder and you can clone them as you like to use other credentials. As you can see in the sample files, much of the details are not specified. These are the autofill fules:

- **manifest** - you must specify the manifest url as the app downloads on the first request.
- **type** - you ***only*** need to specify the type if it is different that the last part of the manifest url (usually they are the same)
- **authority** - you need to set this to a `did:ion:...` DID ***if*** you are doing verification of a Verifiable Credentials of a VC that was issued by another party. If you are verifying credentials you issued yourself, the DID in the manifest is used.

Other important things to consider

- **pin** - do not specify a pin-element unless you are issuing credentials where you want a pin code. Either remove the element or set the length to 0.
- **claims** - do not specify the claims element unless you are issuing credentials using the so called `id_token_hint` model.

### id_token_hint model

With the id_token_hint model, you don't configure a OIDC identity provider .well-known/openid-configuration in your Verifiable Credentials rules file. You manage the authentication yourself as a pre-step to starting and you then pass your required claims to the issuing service.

### Together with Azure AD B2C
To use this sample together with Azure AD B2C, you first needs to build it, which means follow the steps above. 

![API Overview](media/api-b2c-overview.png)

Then you need to deploy B2C Custom Policies that has configuration to add Verifiable Credentials as a Claims Provider and to integrate with this DotNet API. This, you will find in the github repo [https://github.com/cljung/b2c-vc-signin](https://github.com/cljung/b2c-vc-signin). That repo has a node.js issuer/verifier WebApp that uses the VC SDK, but you can skip the `vc` directory and only work with what is in the `b2c` directory. In the instructions on how to edit the B2C policies, it is mentioned that you need to update the `VCServiceUrl` and the `ServiceUrl` to point to your API. That means you need to update it with your `ngrok` url you got when you started the DotNet API in this sample. Otherwise, follow the instructions in [https://github.com/cljung/b2c-vc-signin/blob/main/b2c/README.md](https://github.com/cljung/b2c-vc-signin/blob/main/b2c/README.md) and deploy the B2C Custom Policies

### LogLevel Trace

If you set the LogLevel to `Trace` in the appsettings.*.json file, then the DotNet sample will output all HTTP requests, which will make it convenient for you to study the interaction between components.

![API Overview](media/loglevel-trace.png)