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
        protected const string PresentationRequestConfigFile = "presentation_request_config.json";

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
                string jsonString = ReadFile(PresentationRequestConfigFile);
                if (string.IsNullOrEmpty(jsonString)) {
                    return ReturnErrorMessage( PresentationRequestConfigFile + " not found" );
                }
                // The 'state' variable is the identifier between the Browser session, this API and VC client API doing the validation.
                // It is passed back to the Browser as 'Id' so it can poll for status, and in the presentationCallback (presentation_verified)
                // we use it to correlate which verification that got completed, so we can update the cache and tell the correct Browser session
                // that they are done
                string state = Guid.NewGuid().ToString();
                string nonce = Guid.NewGuid().ToString();
                JObject config = JObject.Parse(jsonString);
                config["authority"] = VCSettings.didVerifier;
                config["registration"]["clientName"] = AppSettings.client_name;
                config["registration"]["logoUrl"] = VCSettings.client_logo_uri;
                config["presentation"]["callback"] = string.Format("{0}/presentationCallback", GetApiPath());
                config["presentation"]["state"] = state;
                config["presentation"]["nonce"] = nonce;
                config["presentation"]["requestedCredentials"][0]["type"] = VCSettings.credentialType;
                config["presentation"]["requestedCredentials"][0]["manifest"] = VCSettings.manifest;
                config["presentation"]["requestedCredentials"][0]["purpose"] = VCSettings.client_purpose;
                config["presentation"]["requestedCredentials"][0]["trustedIssuers"][0] = VCSettings.didIssuer;
                jsonString = JsonConvert.SerializeObject(config);
                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out contents))  {
                    return ReturnErrorMessage( contents );
                }
                JObject apiResp = JObject.Parse(contents);
                //  iOS Authenticator doesn't allow redirects - if you set UsaAkaMS == true in appsettings.json, you don't need this
                apiResp["url"] = apiResp["url"].ToString().Replace("https://aka.ms/vcrequest?", "https://draft.azure-api.net/api/client/v1.0/request?");
                apiResp.Add(new JProperty("id", state));
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
                if (presentationResponse["message"].ToString() == "request_retrieved") {
                    _log.LogTrace("presentationCallback() - request_retrieved");
                    string requestId = presentationResponse["requestId"].ToString();
                    var cacheData = new {
                        status = 1,
                        message = "QR Code is scanned. Waiting for validation..."
                    };
                    CacheJsonObject(requestId, cacheData);
                }
                if (presentationResponse["message"].ToString() == "presentation_verified") {
                    _log.LogTrace("presentationCallback() - presentation_verified");
                    var presentationPath = presentationResponse["presentationReceipt"]["presentation_submission"]["descriptor_map"][0]["path"].ToString();
                    JObject presentation = JWTTokenToJObject( presentationResponse["presentationReceipt"].SelectToken(presentationPath).ToString() );
                    string vcToken = presentation["vp"]["verifiableCredential"][0].ToString();
                    JObject vc = JWTTokenToJObject(vcToken);                    
                    var cacheData = new {
                        status = 2,
                        message = string.Format("{0} {1}", vc["vc"]["credentialSubject"]["firstName"], vc["vc"]["credentialSubject"]["lastName"] ),
                        vc = vcToken
                    };
                    string state = presentationResponse["state"].ToString();
                    CacheJsonObject(state, cacheData );
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
                string state = this.Request.Query["id"];
                if (string.IsNullOrEmpty(state)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                JObject cacheData = null;
                if ( GetCachedJsonObject( state, out cacheData )) { 
                    _log.LogTrace("Have VC validation result");
                    //RemoveCacheValue( state ); // if you're not using B2C integration, uncomment this line
                    return ReturnJson(TransformCacheDataToBrowserResponse(cacheData));
                } else {
                    string requestId = this.Request.Query["requestId"];
                    if (!string.IsNullOrEmpty(requestId) && GetCachedJsonObject( requestId, out cacheData) ) {
                        _log.LogTrace("Have 1st callback");
                        RemoveCacheValue(requestId);
                        return ReturnJson(TransformCacheDataToBrowserResponse(cacheData));
                    }
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
                JObject presentationResponse = JObject.Parse(body);
                string state = presentationResponse["id"].ToString();
                if (string.IsNullOrEmpty(state)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                JObject cacheData = null;
                if ( !GetCachedJsonObject( state, out cacheData )) { 
                    return ReturnErrorB2C("Verifiable Credentials not presented"); // 409
                }
                // remove cache data now, because if we crash, we don't want to get into an infinite loop of crashing 
                RemoveCacheValue(state);
                JObject vc = JWTTokenToJObject( cacheData["vc"].ToString() );
                // these claims are optional
                string sub = null;
                string tid = null;
                string username = null;
                try {
                    tid = vc["vc"]["credentialSubject"]["tid"].ToString();
                    sub = vc["vc"]["credentialSubject"]["sub"].ToString();
                    username = vc["vc"]["credentialSubject"]["username"].ToString();
                } catch ( Exception ex) {
                }
                var b2cResponse = new {
                    id = state,
                    credentialsVerified = true,
                    credentialType = VCSettings.credentialType,
                    displayName = string.Format("{0} {1}", vc["vc"]["credentialSubject"]["firstName"], vc["vc"]["credentialSubject"]["lastName"]),
                    givenName = vc["vc"]["credentialSubject"]["firstName"].ToString(),
                    surName = vc["vc"]["credentialSubject"]["lastName"].ToString(),
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
