using AA.DIDApi.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Threading.Tasks;

namespace AA.DIDApi.Controllers
{
    [Route("api/verifier/[action]")]
    [ApiController]
    public class ApiVerifierController : ApiBaseVCController
    {
        protected const string PresentationRequestConfigFile = "presentation_request_config_v2.json";

        public ApiVerifierController(
            IConfiguration configuration,
            IOptions<AppSettingsModel> appSettings,
            IMemoryCache memoryCache,
            IWebHostEnvironment env,
            ILogger<ApiVerifierController> log) 
                : base(configuration, appSettings, memoryCache, env, log)
        {
            GetPresentationRequest();
        }

        #region Endpoints

        [HttpGet]
        public async Task<ActionResult> Echo()
        {
            TraceHttpRequest();

            try
            {
                JObject manifest = GetPresentationManifest();
                var info = new 
                {
                    date = DateTime.Now.ToString(),
                    host = GetRequestHostName(),
                    api = GetApiPath(),
                    didIssuer = manifest["input"]["issuer"], 
                    didVerifier = manifest["input"]["issuer"], 
                    credentialType = manifest["id"], 
                    displayCard = manifest["display"]["card"],
                    buttonColor = "#000080",
                    contract = manifest["display"]["contract"]
                };

                return ReturnJson(JsonConvert.SerializeObject(info));
            } 
            catch (Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet]
        [Route("/api/verifier/logo.png")]
        public async Task<ActionResult> GetLogo()
        {
            TraceHttpRequest();

            JObject manifest = GetPresentationManifest();
            return Redirect( manifest["display"]["card"]["logo"]["uri"].ToString() );
        }

        [HttpGet("/api/verifier/presentation-request")]
        public async Task<ActionResult> GetPresentationReference()
        {
            TraceHttpRequest();

            try 
            {
                JObject presentationRequest = GetPresentationRequest();
                if ( presentationRequest == null)
                {
                    return ReturnErrorMessage( "Presentation Request Config File not found" );
                }
                
                // The 'state' variable is the identifier between the Browser session, this API and VC client API doing the validation.
                // It is passed back to the Browser as 'Id' so it can poll for status, and in the presentationCallback (presentation_verified)
                // we use it to correlate which verification that got completed, so we can update the cache and tell the correct Browser session
                // that they are done
                string correlationId = Guid.NewGuid().ToString();

                // set details about where we want the VC Client API callback
                var callback = presentationRequest["callback"];
                callback["url"] = string.Format("{0}/presentationCallback", GetApiPath());
                callback["state"] = correlationId;
                callback["nounce"] = Guid.NewGuid().ToString();
                callback["headers"]["my-api-key"] = this.AppSettings.ApiKey;

                string jsonString = JsonConvert.SerializeObject(presentationRequest);
                _log.LogTrace( "VC Client API Request\n{0}", jsonString );

                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out string contents))
                {
                    _log.LogError("VC Client API Error Response\n{0}", contents);
                    return ReturnErrorMessage( contents );
                }

                // pass the response to our caller (but add id)
                JObject apiResp = JObject.Parse(contents);
                apiResp.Add(new JProperty("id", correlationId));
                contents = JsonConvert.SerializeObject(apiResp);
                _log.LogTrace("VC Client API Response\n{0}", contents);
                
                return ReturnJson( contents );
            }
            catch(Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult> PostPresentationCallback()
        {
            TraceHttpRequest();

            try 
            {
                string body = GetRequestBody();
                _log.LogTrace(body);
                JObject presentationResponse = JObject.Parse(body);
                string correlationId = presentationResponse["state"].ToString();

                // request_retrieved == QR code has been scanned and request retrieved from VC Client API
                if (presentationResponse["code"].ToString() == "request_retrieved")
                {
                    _log.LogTrace("presentationCallback() - request_retrieved");
                    string requestId = presentationResponse["requestId"].ToString();
                    var cacheData = new
                    {
                        status = 1,
                        message = "QR Code is scanned. Waiting for validation..."
                    };
                    CacheJsonObjectWithExpiration( correlationId, cacheData );
                }

                // presentation_verified == The VC Client API has received and validateed the presented VC
                if (presentationResponse["code"].ToString() == "presentation_verified")
                {
                    var claims = presentationResponse["issuers"][0]["claims"];
                    _log.LogTrace("presentationCallback() - presentation_verified\n{0}", claims);

                    // build a displayName so we can tell the called who presented their VC
                    JObject vcClaims = (JObject)presentationResponse["issuers"][0]["claims"];
                    string displayName = vcClaims.ContainsKey("displayName")
                        ? vcClaims["displayName"].ToString()
                        : string.Format("{0} {1}", vcClaims["firstName"], vcClaims["lastName"]);

                    var cacheData = new
                    {
                        status = 2,
                        message = displayName,
                        presentationResponse = presentationResponse
                    };
                    
                    CacheJsonObjectWithExpiration(correlationId, cacheData );
                }
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet("/api/verifier/presentation-response-status")]
        public async Task<ActionResult> PostPresentationResponse()
        {
            TraceHttpRequest();
        
            try 
            {
                // This is out caller that call this to poll on the progress and result of the presentation
                string correlationId = this.Request.Query["id"];
                if(string.IsNullOrEmpty(correlationId))
                {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                
                JObject cacheData = null;
                if(GetCachedJsonObject(correlationId, out cacheData))
                { 
                    _log.LogTrace( $"status={cacheData["status"].ToString()}, message={cacheData["message"].ToString()}" );
                    //RemoveCacheValue( state ); // if you're not using B2C integration, uncomment this line
                    return ReturnJson(TransformCacheDataToBrowserResponse(cacheData));
                }
                return new OkResult();
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage( ex.Message );
            }
        }

        [HttpPost("/api/verifier/presentation-response-b2c")]
        public async Task<ActionResult> PostPresentationResponseB2C()
        {
            TraceHttpRequest();
        
            try
            {
                string body = GetRequestBody();
                _log.LogTrace(body);
                JObject b2cRequest = JObject.Parse(body);
                string correlationId = b2cRequest["id"].ToString();
                if (string.IsNullOrEmpty(correlationId))
                {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                
                JObject cacheData = null;
                if(!GetCachedJsonObject(correlationId, out cacheData))
                { 
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
                var credentialType = didIdToken["presentation_submission"]["descriptor_map"][0]["id"].ToString();
                var presentationPath = didIdToken["presentation_submission"]["descriptor_map"][0]["path"].ToString();
                
                JObject presentation = JWTTokenToJObject(didIdToken.SelectToken(presentationPath).ToString());
                string vcToken = presentation["vp"]["verifiableCredential"][0].ToString();
                
                JObject vc = JWTTokenToJObject(vcToken);
                string displayName = vcClaims.ContainsKey("displayName")
                    ? vcClaims["displayName"].ToString()
                    : string.Format("{0} {1}", vcClaims["firstName"], vcClaims["lastName"]);

                // these claims are optional
                string sub = null;
                string tid = null;
                string username = null;

                if (vcClaims.ContainsKey("tid"))
                {
                    tid = vcClaims["tid"].ToString();
                }
                if (vcClaims.ContainsKey("sub"))
                { 
                    sub = vcClaims["sub"].ToString();
                }
                if (vcClaims.ContainsKey("username"))
                { 
                    username = vcClaims["username"].ToString();
                }

                var b2cResponse = new
                {
                    id = correlationId,
                    credentialsVerified = true,
                    credentialType = credentialType,
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

                return ReturnJson(resp);
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        #endregion Endpoints

        #region Helpers

        protected string GetApiPath()
        {
            return string.Format("{0}/api/verifier", GetRequestHostName());
        }

        protected JObject GetPresentationRequest()
        {
            if (GetCachedValue("presentationRequest", out string json))
            {
                return JObject.Parse(json);
            }

            // see if file path was passed on command line
            string presentationRequestFile = _configuration.GetValue<string>("PresentationRequestConfigFile");
            if (string.IsNullOrEmpty(presentationRequestFile))
            {
                presentationRequestFile = PresentationRequestConfigFile;
            }

            if (!System.IO.File.Exists(presentationRequestFile))
            {
                _log.LogError("File not found: {0}", presentationRequestFile);
                return null;
            }

            _log.LogTrace("PresentationRequest file: {0}", presentationRequestFile);
            json = System.IO.File.ReadAllText(presentationRequestFile);
            JObject config = JObject.Parse(json);

            // download manifest and cache it
            HttpStatusCode statusCode = HttpStatusCode.OK;
            if (!HttpGet(config["presentation"]["requestedCredentials"][0]["manifest"].ToString(), out statusCode, out string contents))
            {
                _log.LogError("HttpStatus {0} fetching manifest {1}", statusCode, config["presentation"]["requestedCredentials"][0]["manifest"].ToString());
                return null;
            }
            CacheValueWithNoExpiration("manifestPresentation", contents);
            JObject manifest = JObject.Parse(contents);

            // update presentationRequest from manifest with things that don't change for each request
            if (!config["authority"].ToString().StartsWith("did:ion:"))
            {
                config["authority"] = manifest["input"]["issuer"];
            }
            config["registration"]["clientName"] = AppSettings.client_name;

            var requestedCredentials = config["presentation"]["requestedCredentials"][0];
            if (requestedCredentials["type"].ToString().Length == 0)
            {
                requestedCredentials["type"] = manifest["id"];
            }
            requestedCredentials["trustedIssuers"][0] = manifest["input"]["issuer"]; //VCSettings.didIssuer;

            json = JsonConvert.SerializeObject(config);
            CacheValueWithNoExpiration("presentationRequest", json);
            return config;
        }

        protected JObject GetPresentationManifest()
        {
            if (GetCachedValue("manifestPresentation", out string json))
            {
                return JObject.Parse(json); ;
            }
            return null;
        }

        private string TransformCacheDataToBrowserResponse(JObject cacheData)
        {
            // we do this not to give all the cacheData to the browser
            var browserData = new
            {
                status = cacheData["status"],
                message = cacheData["message"]
            };

            return JsonConvert.SerializeObject(browserData);
        }

        #endregion Helpers
    }
}
