# client-api-test-service-dotnet
Azure AD Verifiable Credentials ASP.Net Core 3.1  sample that uses the new private preview VC Client APIs.

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
  "issuance": {
    "type": "your credentialType",
    "manifest": "https://portableidentitycards.azure-api.net/dev/536279f6-15cc-45f2-be2d-61e352b51eef/portableIdentities/contracts/MyCredentialTypeName",
    "callback": "https://contoso.com/api/issuer/issuanceCallback",
    "nonce": "...",
    "state": "you pass your state here to correlate it when you get the callback",
    "pin": {
      "value": "012345",
      "type": "numeric",
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
- **issuance.type** - the name of your credentialType. Usually matches the last part of the manifest url
- **issuance.manifest** - url of your manifest for your VC. This comes from your defined Verifiable Credential in portal.azure.com
- **issuance.callback** - a callback endpoint in your DotNet API. The VC Client API will call this endpoint when the issuance is completed.
- **issuance.nonce** - The random value to prevent replay attacks
- **issuance.state** - A state value you provide so you can correlate this request when you get callback confirmation
- **issuance.pin** - If you want to require a pin code in the Microsoft Authenticator for this issuance request. This can be useful if it is a self issuing situation where there is no possibility of asking the user to prove their identity via a login. If you don't want to use the pin functionality, you should not have the pin section in the JSON structure.
- **issuance.claims** - optional, extra claims you want to include in the VC.

In the response message from the VC Client API, it will include it's own callback url, which means that once the Microsoft Authenticator has scanned the QR code, it will contact the VC Client API directly and not your DotNet API. The DotNet API will get confirmation via the callback.

### Issuance Callback

In your callback endpoint, you will get a callback with the below message when the QR code is scanned.

```JSON
{"message":"request_retrieved","requestId":"9463da82-e397-45b6-a7a2-2c4223b9fdd0"}
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
  "presentation": {
    "callback": "https://contoso.com/api/verifier/presentationCallback",
    "nonce": "...",
    "state": "you pass your state here to correlate it when you get the callback",
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

- **authority** vs **trustedIssuers** - The Verifier and the Issuer may be two different entities. For example, the Verifier might be a online service, like a car rental service, while the DID it is asking for is the issuing entity forr drivers licenses. Note that `trustedIssuers` is a collection of DIDs, which means you can ask for multiple VCs from the user
- **requestedCredentials** - please also note that the `requestedCredentials` is a collection too, which means you can ask to create a presentation request that contains multiple DIDs.

### Verification Callback

In your callback endpoint, you will get a callback with the below message when the QR code is scanned.

When the QR code is scanned, you get a short callback like this.
```JSON
{"message":"request_retrieved","requestId":"c18d8035-3fc8-4c27-a5db-9801e6232569"}
```

Once the VC is verified, you get a second, more complete, callback which contains all the details on what whas presented by the user.

```JSON
{
    "message":"presentation_verified",
    "claims":
    {
        "YourCredentialType.displayName":"Alice Contoso",
        "YourCredentialType.sub":"...",
        "YourCredentialType.tid":"...",
        "YourCredentialType.username":"alice@contoso.com",
        "YourCredentialType.lastName":"Contoso",
        "YourCredentialType.firstName":"alice"
    },
    "state":"4c9320dc-d34a-4e1a-b17d-179bdafe5895",
    "nonce":"078ff71a-7eeb-4a92-8ee2-6f5aa5fb40f5",
    "presentationReceipt":{
        "state":"...",
        "exp":1620579579,
        "attestations":
        {
            "presentations":
            {
                "YourCredentialType":"...JWT Token of VC..."
            }
        },
        "nonce":"078ff71a-7eeb-4a92-8ee2-6f5aa5fb40f5",
        "sub_jwk": { ...jwt details ... },
        "jti":"2F0898A5-7B46-4BCA-897D-6E3AB643DDD9",
        "iss":"https://self-issued.me",
        "sub":"i1HVr0lamHTuWpUEOL06454yDIZfNhTP4tommz_7KFg",
        "presentation_submission":
        {
            "descriptor_map":[
                {
                    "path":"$.attestations.presentations.YourCredentialType","id":"YourCredentialType",
                    "encoding":"base64Url",
                    "format":"JWT"
                }
            ]
        },
        "did":"did:ion: ...of the user",
        "iat":1620576579,
        "aud":"https://draft.azure-api.net/xyz/api/client/v1.0/present"
    }
}
```
Some notable attributes in the message:
- **claims** - parsed claims from the VC
- **presentation_submission.presentations.path** - JSON path to the VCs JWT token
- **did** - the DID of the user who presented the VC

## Running the sample

### Standalone
To run the sample standalone, just clone the repository, compile & run it. It's callback endpoint must be publically reachable, and for that reason, use `ngrok` as a reverse proxy to read your app.

```Powershell
git clone https://github.com/cljung/client-api-test-service-dotnet.git
cd client-api-test-service-dotnet
dotnet build "client-api-test-service-dotnet.csproj" -c Debug -o .\bin\Debug\netcoreapp3.1
dotnet run
```

Then, open a separate command prompt and run the following command

```Powershell
ngrok http 5002
```

Grab, the url in the ngrok output (like `https://96a139d4199b.ngrok.io`) and Browse to it.

### Together with Azure AD B2C
To use this sample together with Azure AD B2C, you first needs to build it, which means follow the steps above. 

![API Overview](media/api-b2c-overview.png)

Then you need to deploy B2C Custom Policies that has configuration to add Verifiable Credentials as a Claims Provider and to integrate with this DotNet API. This, you will find in the github repo [https://github.com/cljung/b2c-vc-signin](https://github.com/cljung/b2c-vc-signin). That repo has a node.js issuer/verifier WebApp that uses the VC SDK, but you can skip the `vc` directory and only work with what is in the `b2c` directory. In the instructions on how to edit the B2C policies, it is mentioned that you need to update the `VCServiceUrl` and the `ServiceUrl` to point to your API. That means you need to update it with your `ngrok` url you got when you started the DotNet API in this sample. Otherwise, follow the instructions in [https://github.com/cljung/b2c-vc-signin/blob/main/b2c/README.md](https://github.com/cljung/b2c-vc-signin/blob/main/b2c/README.md) and deploy the B2C Custom Policies


### Docker build

To run it locally with Docker
```
docker build -t client-api-test-service-dotnet:v1.0 .
docker run --rm -it -p 5002:80 client-api-test-service-dotnet:v1.0
```

Then, open a separate command prompt and run the following command

```Powershell
ngrok http 5002
```

Grab, the url in the ngrok output (like `https://96a139d4199b.ngrok.io`) and Browse to it.

## appsettings.json

The configuration you have in the `appsettings.json` file determinds which CredentialType you will be using. If you want to use your own credentials, you need to update this file. The appsettings.Development.json contains settings that work with a dev/test B2C credential.
 
```JSON
{
  "Logging": {
    "LogLevel": {
      "Default": "Trace",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "AllowedHosts": "*",
  "AppSettings.Expert": {
    "ApiEndpoint": "https://draft.azure-api.net/xyz/api/client/v1.0/request",
    "ApiKey": "MyApiKey",
    "CookieKey": "state",
    "CookieExpiresInSeconds": 7200,
    "CacheExpiresInSeconds": 300,
    "PinCodeLength": 0,
    "didIssuer": "did:ion:EiAUeAySrc1qgPucLYI_ytfudT8bFxUETNolzz4PCdy1bw:eyJkZWx0YSI6eyJwYXRjaGVzIjpbeyJhY3Rpb24iOiJyZXBsYWNlIiwiZG9jdW1lbnQiOnsicHVibGljS2V5cyI6W3siaWQiOiJzaWdfMjRiYjMwNzQiLCJwdWJsaWNLZXlKd2siOnsiY3J2Ijoic2VjcDI1NmsxIiwia3R5IjoiRUMiLCJ4IjoiRDlqYUgwUTFPZW1XYVVfeGtmRzBJOVoyYnctOFdLUFF2TWt2LWtkdjNxUSIsInkiOiJPclVUSzBKSWN0UnFQTHRCQlQxSW5iMTdZS29sSFJvX1kyS0Zfb3YyMEV3In0sInB1cnBvc2VzIjpbImF1dGhlbnRpY2F0aW9uIiwiYXNzZXJ0aW9uTWV0aG9kIl0sInR5cGUiOiJFY2RzYVNlY3AyNTZrMVZlcmlmaWNhdGlvbktleTIwMTkifV0sInNlcnZpY2VzIjpbeyJpZCI6ImxpbmtlZGRvbWFpbnMiLCJzZXJ2aWNlRW5kcG9pbnQiOnsib3JpZ2lucyI6WyJodHRwczovL2RpZC53b29kZ3JvdmVkZW1vLmNvbS8iXX0sInR5cGUiOiJMaW5rZWREb21haW5zIn1dfX1dLCJ1cGRhdGVDb21taXRtZW50IjoiRWlBeWF1TVgzRWtBcUg2RVFUUEw4SmQ4alVvYjZXdlZrNUpSamdodEVYWHhDQSJ9LCJzdWZmaXhEYXRhIjp7ImRlbHRhSGFzaCI6IkVpQ1NvajVqSlNOUjBKU0tNZEJ1Y2RuMlh5U2ZaYndWVlNIWUNrREllTHV5NnciLCJyZWNvdmVyeUNvbW1pdG1lbnQiOiJFaUR4Ym1ELTQ5cEFwMDBPakd6VXdoNnY5ZjB5cnRiaU5TbXA3dldwbTREVHpBIn19",
    "didVerifier": "did:ion:EiAUeAySrc1qgPucLYI_ytfudT8bFxUETNolzz4PCdy1bw:eyJkZWx0YSI6eyJwYXRjaGVzIjpbeyJhY3Rpb24iOiJyZXBsYWNlIiwiZG9jdW1lbnQiOnsicHVibGljS2V5cyI6W3siaWQiOiJzaWdfMjRiYjMwNzQiLCJwdWJsaWNLZXlKd2siOnsiY3J2Ijoic2VjcDI1NmsxIiwia3R5IjoiRUMiLCJ4IjoiRDlqYUgwUTFPZW1XYVVfeGtmRzBJOVoyYnctOFdLUFF2TWt2LWtkdjNxUSIsInkiOiJPclVUSzBKSWN0UnFQTHRCQlQxSW5iMTdZS29sSFJvX1kyS0Zfb3YyMEV3In0sInB1cnBvc2VzIjpbImF1dGhlbnRpY2F0aW9uIiwiYXNzZXJ0aW9uTWV0aG9kIl0sInR5cGUiOiJFY2RzYVNlY3AyNTZrMVZlcmlmaWNhdGlvbktleTIwMTkifV0sInNlcnZpY2VzIjpbeyJpZCI6ImxpbmtlZGRvbWFpbnMiLCJzZXJ2aWNlRW5kcG9pbnQiOnsib3JpZ2lucyI6WyJodHRwczovL2RpZC53b29kZ3JvdmVkZW1vLmNvbS8iXX0sInR5cGUiOiJMaW5rZWREb21haW5zIn1dfX1dLCJ1cGRhdGVDb21taXRtZW50IjoiRWlBeWF1TVgzRWtBcUg2RVFUUEw4SmQ4alVvYjZXdlZrNUpSamdodEVYWHhDQSJ9LCJzdWZmaXhEYXRhIjp7ImRlbHRhSGFzaCI6IkVpQ1NvajVqSlNOUjBKU0tNZEJ1Y2RuMlh5U2ZaYndWVlNIWUNrREllTHV5NnciLCJyZWNvdmVyeUNvbW1pdG1lbnQiOiJFaUR4Ym1ELTQ5cEFwMDBPakd6VXdoNnY5ZjB5cnRiaU5TbXA3dldwbTREVHpBIn19",
    "manifest": "https://beta.did.msidentity.com/v1.0/3c32ed40-8a10-465b-8ba4-0b1e86882668/verifiableCredential/contracts/VerifiedCredentialExpert",
    "credentialType": "VerifiedCredentialExpert",
    "client_name": "DotNet Client API Verifier",
    "client_logo_uri": "https://didcustomerplayground.blob.core.windows.net/public/VerifiedCredentialExpert_icon.png",
    "client_tos_uri": "https://www.microsoft.com/servicesagreement",
    "client_purpose": "To check if you know how to use verifiable credentials."
  }
}
``` 