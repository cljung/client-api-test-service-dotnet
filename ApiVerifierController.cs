using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using client_api_test_service_dotnet.Models;
using Microsoft.Extensions.Logging;

namespace client_api_test_service_dotnet
{
    [Route("api/verifier/[action]")]
    [ApiController]
    public class ApiVerifierController : ApiBaseVCController
    {
        protected const string PresentationRequestConfigFile = "presentation_request_config_v2.json";

        public ApiVerifierController(IOptions<AppSettingsModelVC> vcSettings, IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, IWebHostEnvironment env, ILogger<ApiVerifierController> log) : base(vcSettings, appSettings, memoryCache, env, log)
        {            
        }

        protected string GetApiPath() {
            return string.Format("{0}/api/verifier", GetRequestHostName());
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// REST APIs
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        [HttpGet]
        public async Task<ActionResult> echo() {
            TraceHttpRequest();
            try
            {
                JObject manifest = JObject.Parse(GetDidManifest());
                var info = new {
                    date = DateTime.Now.ToString(),
                    host = GetRequestHostName(),
                    api = GetApiPath(),
                    didIssuer = VCSettings.didIssuer,
                    didVerifier = VCSettings.didVerifier,
                    credentialType = VCSettings.credentialType,
                    client_purpose = VCSettings.client_purpose,
                    displayCard = manifest["display"]["card"],
                    buttonColor = "#000080",
                    contract = manifest["display"]["contract"]
                };
                return ReturnJson(JsonConvert.SerializeObject(info));
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }
        [HttpGet]
        [Route("/logo.png")]
        public async Task<ActionResult> logo() {
            TraceHttpRequest();
            return Redirect(VCSettings.client_logo_uri);
        }

        [HttpGet]
        public async Task<ActionResult> presentation() {
            TraceHttpRequest();
            try {
                return SendStaticJsonFile(PresentationRequestConfigFile);
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }

        [HttpGet("/api/verifier/presentation-request")]
        public async Task<ActionResult> presentationReference() {
            TraceHttpRequest();
            try {
                string jsonString = ReadFile( PresentationRequestConfigFile );
                if (string.IsNullOrEmpty(jsonString)) {
                    return ReturnErrorMessage( PresentationRequestConfigFile + " not found" );
                }
                // The 'state' variable is the identifier between the Browser session, this API and VC client API doing the validation.
                // It is passed back to the Browser as 'Id' so it can poll for status, and in the presentationCallback (presentation_verified)
                // we use it to correlate which verification that got completed, so we can update the cache and tell the correct Browser session
                // that they are done
                string correlationId = Guid.NewGuid().ToString();
                JObject config = JObject.Parse(jsonString);
                config["authority"] = VCSettings.didVerifier;
                config["registration"]["clientName"] = AppSettings.client_name;
                config["registration"]["logoUrl"] = VCSettings.client_logo_uri;

                // set details about where we want the VC Client API callback
                var callback = config["callback"];
                callback["url"] = string.Format("{0}/presentationCallback", GetApiPath());
                callback["state"] = correlationId;
                callback["nonce"] = Guid.NewGuid().ToString();
                callback["headers"]["my-api-key"] = this.AppSettings.ApiKey;

                // set details about the VC we are asking for
                var requestedCredentials = config["presentation"]["requestedCredentials"][0];
                requestedCredentials["type"] = VCSettings.credentialType;
                requestedCredentials["manifest"] = VCSettings.manifest;
                requestedCredentials["purpose"] = VCSettings.client_purpose;
                requestedCredentials["trustedIssuers"][0] = VCSettings.didIssuer;

                jsonString = JsonConvert.SerializeObject(config);
                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out contents))  {
                    return ReturnErrorMessage( contents );
                }
                // pass the response to our caller
                JObject apiResp = JObject.Parse(contents);
                apiResp.Add(new JProperty("id", correlationId));
                apiResp.Add(new JProperty("link", apiResp["url"].ToString()));
                contents = JsonConvert.SerializeObject(apiResp);
                return ReturnJson( contents );
            }  catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult> presentationCallback() {
            TraceHttpRequest();
            try {
                string body = GetRequestBody();
                _log.LogTrace(body);
                JObject presentationResponse = JObject.Parse(body);
                string correlationId = presentationResponse["state"].ToString();

                // request_retrieved == QR code has been scanned and request retrieved from VC Client API
                if (presentationResponse["code"].ToString() == "request_retrieved") {
                    _log.LogTrace("presentationCallback() - request_retrieved");
                    string requestId = presentationResponse["requestId"].ToString();
                    var cacheData = new {
                        status = 1,
                        message = "QR Code is scanned. Waiting for validation..."
                    };
                    CacheJsonObject(correlationId, cacheData);
                }

                // presentation_verified == The VC Client API has received and validateed the presented VC
                if (presentationResponse["code"].ToString() == "presentation_verified") {
                    var claims = presentationResponse["issuers"][0]["claims"];
                    _log.LogTrace("presentationCallback() - presentation_verified\n{0}", claims );

                    var cacheData = new {
                        status = 2,
                        message = string.Format("{0} {1}", claims["firstName"].ToString(), claims["lastName"].ToString() ),
                        presentationResponse = presentationResponse
                    };
                    CacheJsonObject(correlationId, cacheData );
                }
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }
        [HttpGet("/api/verifier/presentation-response")]
        public async Task<ActionResult> presentationResponse() {
            TraceHttpRequest();
            try {
                // This is out caller that call this to poll on the progress and result of the presentation
                string correlationId = this.Request.Query["id"];
                if (string.IsNullOrEmpty(correlationId)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                JObject cacheData = null;
                if ( GetCachedJsonObject(correlationId, out cacheData )) { 
                    _log.LogTrace( $"status={cacheData["status"].ToString()}, message={cacheData["message"].ToString()}" );
                    //RemoveCacheValue( state ); // if you're not using B2C integration, uncomment this line
                    return ReturnJson(TransformCacheDataToBrowserResponse(cacheData));
                }
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }

        private string TransformCacheDataToBrowserResponse( JObject cacheData ) {
            // we do this not to give all the cacheData to the browser
            var browserData = new {
                status = cacheData["status"],
                message = cacheData["message"]
            };
            return JsonConvert.SerializeObject(browserData);
        }
        [HttpPost("/api/verifier/presentation-response-b2c")]
        public async Task<ActionResult> presentationResponseB2C() {
            TraceHttpRequest();
            try {
                string body = GetRequestBody();
                _log.LogTrace(body);
                JObject b2cRequest = JObject.Parse(body);
                string correlationId = b2cRequest["id"].ToString();
                if (string.IsNullOrEmpty(correlationId)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                JObject cacheData = null;
                if ( !GetCachedJsonObject(correlationId, out cacheData )) { 
                    return ReturnErrorB2C("Verifiable Credentials not presented"); // 409
                }
                // remove cache data now, because if we crash, we don't want to get into an infinite loop of crashing 
                RemoveCacheValue(correlationId);

                // get the payload from the presentation-response callback
                var presentationResponse = cacheData["presentationResponse"];
                // get the claims tha the VC Client API provides to us from the presented VC
                JObject vcClaims = (JObject)presentationResponse["issuers"][0]["claims"];

                // get the token that was presented and dig out the VC credential from it since we want to return the
                // Issuer DID and the holders DID to B2C
                JObject didIdToken = JWTTokenToJObject(presentationResponse["receipt"]["id_token"].ToString());
                var presentationPath = didIdToken["presentation_submission"]["descriptor_map"][0]["path"].ToString();
                JObject presentation = JWTTokenToJObject(didIdToken.SelectToken(presentationPath).ToString());
                string vcToken = presentation["vp"]["verifiableCredential"][0].ToString();
                JObject vc = JWTTokenToJObject(vcToken);

                string displayName = null;
                if (vcClaims.ContainsKey("displayName"))
                     displayName = vcClaims["displayName"].ToString();
                else displayName = string.Format("{0} {1}", vcClaims["firstName"], vcClaims["lastName"]);

                // these claims are optional
                string sub = null;
                string tid = null;
                string username = null;

                if (vcClaims.ContainsKey("tid")) 
                    tid = vcClaims["tid"].ToString();
                if (vcClaims.ContainsKey("sub"))
                    sub = vcClaims["sub"].ToString();
                if (vcClaims.ContainsKey("username"))
                    username = vcClaims["username"].ToString();

                var b2cResponse = new {
                    id = correlationId,
                    credentialsVerified = true,
                    credentialType = VCSettings.credentialType,
                    displayName = displayName,
                    givenName = vcClaims["firstName"].ToString(),
                    surName = vcClaims["lastName"].ToString(),
                    iss = vc["iss"].ToString(),
                    sub = vc["sub"].ToString(),
                    key = vc["sub"].ToString().Replace("did:ion:","did.ion.").Split(":")[0],
                    oid = sub,
                    tid = tid,
                    username = username
                };
                string resp = JsonConvert.SerializeObject(b2cResponse);
                _log.LogTrace(resp);
                return ReturnJson( resp );
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }
    } // cls
} // ns
