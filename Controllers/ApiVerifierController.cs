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
using System.IO;
using System.Threading.Tasks;

namespace AA.DIDApi.Controllers
{
    [Route("api/verifier")]
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
            GetPresentationRequest().GetAwaiter().GetResult();
        }

        #region Endpoints

        [HttpGet("echo")]
        public ActionResult Echo()
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
        [Route("logo.png")]
        public ActionResult GetLogo()
        {
            TraceHttpRequest();

            JObject manifest = GetPresentationManifest();
            return Redirect(manifest["display"]["card"]["logo"]["uri"].ToString());
        }

        [HttpGet("presentation-request")]
        public async Task<ActionResult> GetPresentationReference()
        {
            TraceHttpRequest();

            try 
            {
                JObject presentationRequest = null;
                string exception = string.Empty;
                try
                {
                    presentationRequest = await GetPresentationRequest();
                }
                catch (Exception ex)
                {
                    exception = ex.Message;
                }
                if (presentationRequest == null)
                {
                    return ReturnErrorMessage($"Presentation Request Config File not found: {exception}");
                }
                
                // The 'state' variable is the identifier between the Browser session, this API and VC client API doing the validation.
                // It is passed back to the Browser as 'Id' so it can poll for status, and in the presentationCallback (presentation_verified)
                // we use it to correlate which verification that got completed, so we can update the cache and tell the correct Browser session
                // that they are done
                string correlationId = Guid.NewGuid().ToString();

                // set details about where we want the VC Client API callback
                var callback = presentationRequest["callback"];
                callback["url"] = $"{GetApiPath()}/presentationCallback";
                callback["state"] = correlationId;
                callback["nounce"] = Guid.NewGuid().ToString();
                callback["headers"]["my-api-key"] = this.AppSettings.ApiKey;

                string jsonString = JsonConvert.SerializeObject(presentationRequest);
                HttpActionResponse httpPostResponse = await HttpPostAsync(jsonString);
                if (!httpPostResponse.IsSuccessStatusCode)
                {
                    _log.LogError($"VC Client API Error Response\n{httpPostResponse.ResponseContent}\n{httpPostResponse.StatusCode}");
                    return ReturnErrorMessage(httpPostResponse.ResponseContent);
                }

                // pass the response to our caller (but add id)
                JObject apiResp = JObject.Parse(httpPostResponse.ResponseContent);
                apiResp.Add(new JProperty("id", correlationId));
                httpPostResponse.ResponseContent = JsonConvert.SerializeObject(apiResp);
                
                _log.LogTrace($"VC Client API Response\n{httpPostResponse.ResponseContent}");
                return ReturnJson(httpPostResponse.ResponseContent);
            }
            catch(Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost("presentationCallback")]
        public async Task<ActionResult> PostPresentationCallback()
        {
            TraceHttpRequest();

            try 
            {
                string body = await GetRequestBodyAsync();
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
                    CacheJsonObjectWithExpiration(correlationId, cacheData);
                }

                // presentation_verified == The VC Client API has received and validateed the presented VC
                if (presentationResponse["code"].ToString() == "presentation_verified")
                {
                    var claims = presentationResponse["issuers"][0]["claims"];
                    _log.LogTrace($"presentationCallback() - presentation_verified\n{claims}");

                    // build a displayName so we can tell the called who presented their VC
                    JObject vcClaims = (JObject)presentationResponse["issuers"][0]["claims"];
                    string displayName = vcClaims.ContainsKey("displayName")
                        ? vcClaims["displayName"].ToString()
                        : $"{vcClaims["firstName"]} {vcClaims["lastName"]}";

                    var cacheData = new
                    {
                        status = 2,
                        message = displayName,
                        presentationResponse = presentationResponse
                    };
                    
                    CacheJsonObjectWithExpiration(correlationId, cacheData);
                }

                return new OkResult();
            } 
            catch (Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet("presentation-response-status")]
        public ActionResult PostPresentationResponse()
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

                if (GetCachedJsonObject(correlationId, out JObject cacheData))
                {
                    _log.LogTrace($"status={cacheData["status"]}, message={cacheData["message"]}");
                    //RemoveCacheValue(state); // if you're not using B2C integration, uncomment this line
                    return ReturnJson(TransformCacheDataToBrowserResponse(cacheData));
                }

                return new OkResult();
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost("presentation-response-b2c")]
        public async Task<ActionResult> PostPresentationResponseB2C()
        {
            TraceHttpRequest();
        
            try
            {
                string body = await GetRequestBodyAsync();
                _log.LogTrace(body);
                JObject b2cRequest = JObject.Parse(body);
                string correlationId = b2cRequest["id"].ToString();
                if (string.IsNullOrEmpty(correlationId))
                {
                    return ReturnErrorMessage("Missing argument 'id'");
                }

                if (!GetCachedJsonObject(correlationId, out JObject cacheData))
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
                    : $"{vcClaims["firstName"]} {vcClaims["lastName"]}";

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
            return $"{GetRequestHostName()}/api/verifier";
        }

        protected async Task<JObject> GetPresentationRequest()
        {
            if (GetCachedValue("presentationRequest", out string json))
            {
                _log.LogInformation($"presentationRequest retrieved from Cache: {json}");
                return JObject.Parse(json);
            }

            // see if file path was passed on command line
            string presentationRequestFile = _configuration.GetValue<string>("PresentationRequestConfigFile");
            if (string.IsNullOrEmpty(presentationRequestFile))
            {
                presentationRequestFile = PresentationRequestConfigFile;
            }

            string fileLocation = Directory.GetParent(typeof(Program).Assembly.Location).FullName;
            _log.LogInformation($"FileLocation: {fileLocation}");
            string file = $"{fileLocation}\\{presentationRequestFile}";
            if (!System.IO.File.Exists(file))
            {
                _log.LogError($"File not found: {presentationRequestFile}");
                return null;
            }

            _log.LogTrace($"PresentationRequest file: {presentationRequestFile}");
            json = System.IO.File.ReadAllText(file);
            _log.LogInformation($"Json read from config: {json}");
            JObject config = JObject.Parse(json);
            _log.LogInformation($"Config: {config}");

            // download manifest and cache it
            _log.LogInformation($"Executing HttpGetAsync");
            HttpActionResponse httpGetResponse = await HttpGetAsync(config["presentation"]["requestedCredentials"][0]["manifest"].ToString());
            _log.LogInformation($"HttpGetResponse ResponseContent: {httpGetResponse?.ResponseContent}");
            if (!httpGetResponse.IsSuccessStatusCode)
            {
                _log.LogError($"HttpStatus {httpGetResponse.StatusCode} fetching manifest {config["presentation"]["requestedCredentials"][0]["manifest"]}");
                return null;
            }

            CacheValueWithNoExpiration("manifestPresentation", httpGetResponse.ResponseContent);
            JObject manifest = JObject.Parse(httpGetResponse.ResponseContent);

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

        /*
        /// <summary>
        /// Hardcoded values
        /// </summary>
        protected async Task<JObject> GetPresentationRequest()
        {
            string json = "{\"authority\":\"did:ion:EiD_ZULUvK_3eTfWfq97cc87DeU8J0AEzIaSuFLRAHzXoQ:eyJkZWx0YSI6eyJwYXRjaGVzIjpbeyJhY3Rpb24iOiJyZXBsYWNlIiwiZG9jdW1lbnQiOnsicHVibGljS2V5cyI6W3siaWQiOiJzaWdfY2FiNjVhYTAiLCJwdWJsaWNLZXlKd2siOnsiY3J2Ijoic2VjcDI1NmsxIiwia3R5IjoiRUMiLCJ4IjoiU21xMVZNNXp0RUVpZGpoWE5uckxub3N5TkI2MEVaV05CWXdUY3dQazU3YyIsInkiOiJSeFd3QlNyQjBaWl9MdndKVGpMamRqUEtTMXlZQjgzOUZIckFWUm9EYW9nIn0sInB1cnBvc2VzIjpbImF1dGhlbnRpY2F0aW9uIiwiYXNzZXJ0aW9uTWV0aG9kIl0sInR5cGUiOiJFY2RzYVNlY3AyNTZrMVZlcmlmaWNhdGlvbktleTIwMTkifV0sInNlcnZpY2VzIjpbeyJpZCI6ImxpbmtlZGRvbWFpbnMiLCJzZXJ2aWNlRW5kcG9pbnQiOnsib3JpZ2lucyI6WyJodHRwczovL3ZjYXBpZGV2Lndvb2Rncm92ZWRlbW8uY29tLyJdfSwidHlwZSI6IkxpbmtlZERvbWFpbnMifV19fV0sInVwZGF0ZUNvbW1pdG1lbnQiOiJFaUIza0VSRUlwSWdodFp2SUxaemFlMDF1NGtXSVNnWHFqN0lFUFVfUGNBUnJBIn0sInN1ZmZpeERhdGEiOnsiZGVsdGFIYXNoIjoiRWlEcGMtelNRcHJYMGhacDdlMC1QXzhwWjkzdm9EZXhwLVo1ZXVtbFhzN2hQUSIsInJlY292ZXJ5Q29tbWl0bWVudCI6IkVpQWtKOTNBLThyeXF3X0VqSVRuSEpqdkhvZ016N2YtTlRZWXlhOENVMEdYdWcifX0\",\"includeQRCode\":false,\"registration\":{\"clientName\":\"DotNet Client API Verifier\"},\"callback\":{\"url\":\"...set at runtime...\",\"state\":\"...set at runtime...\",\"headers\":{\"my-api-key\":\"blabla\"}},\"presentation\":{\"includeReceipt\":true,\"requestedCredentials\":[{\"type\":\"Cljungdemob2cMembership\",\"manifest\":\"https://beta.did.msidentity.com/v1.0/9885457a-2026-4e2c-a47e-32ff52ea0b8d/verifiableCredential/contracts/Cljungdemob2cMembership\",\"purpose\":\"the purpose why the verifier asks for a VC\",\"trustedIssuers\":[\"did:ion:EiDDDbBaSlIvrzluYEW4mqnxpM09-MrJrcn6w3EVG_cMIQ:eyJkZWx0YSI6eyJwYXRjaGVzIjpbeyJhY3Rpb24iOiJyZXBsYWNlIiwiZG9jdW1lbnQiOnsicHVibGljS2V5cyI6W3siaWQiOiJzaWdfNTg0OGUxZGIiLCJwdWJsaWNLZXlKd2siOnsiY3J2Ijoic2VjcDI1NmsxIiwia3R5IjoiRUMiLCJ4IjoiX3ZzVjB4V0tWMDMxWlZTaVJTb2dabHB3QjZVRThfLWZ2WU1vcXNQRDNYMCIsInkiOiItMXJxZEx4TUpZN081UHA3R21sSWhWSWVtVlNnQnpjaEhObi1mZE02MDVrIn0sInB1cnBvc2VzIjpbImF1dGhlbnRpY2F0aW9uIiwiYXNzZXJ0aW9uTWV0aG9kIl0sInR5cGUiOiJFY2RzYVNlY3AyNTZrMVZlcmlmaWNhdGlvbktleTIwMTkifV0sInNlcnZpY2VzIjpbeyJpZCI6ImxpbmtlZGRvbWFpbnMiLCJzZXJ2aWNlRW5kcG9pbnQiOnsib3JpZ2lucyI6WyJodHRwczovL2Zhd2x0eXRvd2VyczIuY29tLyJdfSwidHlwZSI6IkxpbmtlZERvbWFpbnMifV19fV0sInVwZGF0ZUNvbW1pdG1lbnQiOiJFaUJFUi1UZC1LTkdIRGFMcDBNUDFFdzUtd3ZQa3RrMmVMVFlPMkRPazJDZElnIn0sInN1ZmZpeERhdGEiOnsiZGVsdGFIYXNoIjoiRWlEcmJxalBtVDZSaEg2aEhRdkZuQ1drRVFzaU9oUG11SEhVR3IyeDhYM2ljQSIsInJlY292ZXJ5Q29tbWl0bWVudCI6IkVpQkpDUjUycHV4SldoZVNaZnFqNFgtMkNKSkdwRHY1ZEY0S1VXOEZjN0ZQZFEifX0\"]}]}}";
            JObject config = JObject.Parse(json);

            JObject manifest = JObject.Parse("{\"id\":\"Cljungdemob2cMembership\",\"display\":{\"locale\":\"en-US\",\"contract\":\"https://beta.did.msidentity.com/v1.0/9885457a-2026-4e2c-a47e-32ff52ea0b8d/verifiableCredential/contracts/Cljungdemob2cMembership\",\"card\":{\"title\":\"CljungdemoB2C Membership\",\"issuedBy\":\"cljungdemob2c\",\"backgroundColor\":\"#C0C0C0\",\"textColor\":\"#ffffff\",\"logo\":{\"uri\":\"https://cljungdemob2c.blob.core.windows.net/uxcust/templates/images/snoopy-small.jpg\",\"description\":\"cljungdemob2c Logo\"},\"description\":\"Use your verified credential card to prove you are a cljungdemob2c member.\"},\"consent\":{\"title\":\"Do you want to get your cljungdemob2c membership card?\",\"instructions\":\"Sign in with your account to get your card.\"},\"claims\":{\"vc.credentialSubject.firstName\":{\"type\":\"String\",\"label\":\"First name\"},\"vc.credentialSubject.lastName\":{\"type\":\"String\",\"label\":\"Last name\"},\"vc.credentialSubject.country\":{\"type\":\"String\",\"label\":\"Country\"},\"vc.credentialSubject.sub\":{\"type\":\"String\",\"label\":\"sub\"},\"vc.credentialSubject.tid\":{\"type\":\"String\",\"label\":\"tid\"},\"vc.credentialSubject.displayName\":{\"type\":\"String\",\"label\":\"displayName\"},\"vc.credentialSubject.username\":{\"type\":\"String\",\"label\":\"username\"}},\"id\":\"display\"},\"input\":{\"credentialIssuer\":\"https://beta.did.msidentity.com/v1.0/9885457a-2026-4e2c-a47e-32ff52ea0b8d/verifiableCredential/card/issue\",\"issuer\":\"did:ion:EiDDDbBaSlIvrzluYEW4mqnxpM09-MrJrcn6w3EVG_cMIQ:eyJkZWx0YSI6eyJwYXRjaGVzIjpbeyJhY3Rpb24iOiJyZXBsYWNlIiwiZG9jdW1lbnQiOnsicHVibGljS2V5cyI6W3siaWQiOiJzaWdfNTg0OGUxZGIiLCJwdWJsaWNLZXlKd2siOnsiY3J2Ijoic2VjcDI1NmsxIiwia3R5IjoiRUMiLCJ4IjoiX3ZzVjB4V0tWMDMxWlZTaVJTb2dabHB3QjZVRThfLWZ2WU1vcXNQRDNYMCIsInkiOiItMXJxZEx4TUpZN081UHA3R21sSWhWSWVtVlNnQnpjaEhObi1mZE02MDVrIn0sInB1cnBvc2VzIjpbImF1dGhlbnRpY2F0aW9uIiwiYXNzZXJ0aW9uTWV0aG9kIl0sInR5cGUiOiJFY2RzYVNlY3AyNTZrMVZlcmlmaWNhdGlvbktleTIwMTkifV0sInNlcnZpY2VzIjpbeyJpZCI6ImxpbmtlZGRvbWFpbnMiLCJzZXJ2aWNlRW5kcG9pbnQiOnsib3JpZ2lucyI6WyJodHRwczovL2Zhd2x0eXRvd2VyczIuY29tLyJdfSwidHlwZSI6IkxpbmtlZERvbWFpbnMifV19fV0sInVwZGF0ZUNvbW1pdG1lbnQiOiJFaUJFUi1UZC1LTkdIRGFMcDBNUDFFdzUtd3ZQa3RrMmVMVFlPMkRPazJDZElnIn0sInN1ZmZpeERhdGEiOnsiZGVsdGFIYXNoIjoiRWlEcmJxalBtVDZSaEg2aEhRdkZuQ1drRVFzaU9oUG11SEhVR3IyeDhYM2ljQSIsInJlY292ZXJ5Q29tbWl0bWVudCI6IkVpQkpDUjUycHV4SldoZVNaZnFqNFgtMkNKSkdwRHY1ZEY0S1VXOEZjN0ZQZFEifX0\",\"attestations\":{\"idTokens\":[{\"id\":\"https://login.fawltytowers2.com/cljungdemob2c.onmicrosoft.com/B2C_1A_UX_signup_signin/v2.0/.well-known/openid-configuration\",\"encrypted\":false,\"claims\":[{\"claim\":\"name\",\"required\":false,\"indexed\":false},{\"claim\":\"sub\",\"required\":false,\"indexed\":false},{\"claim\":\"tid\",\"required\":false,\"indexed\":false},{\"claim\":\"email\",\"required\":false,\"indexed\":false},{\"claim\":\"family_name\",\"required\":false,\"indexed\":false},{\"claim\":\"given_name\",\"required\":false,\"indexed\":false},{\"claim\":\"ctry\",\"required\":false,\"indexed\":false}],\"required\":false,\"configuration\":\"https://login.fawltytowers2.com/cljungdemob2c.onmicrosoft.com/B2C_1A_UX_signup_signin/v2.0/.well-known/openid-configuration\",\"client_id\":\"ac455c95-6f77-4dd5-bffe-117329946906\",\"redirect_uri\":\"vcclient://openid\",\"scope\":\"openid\"}],\"_hasAttestations\":false},\"id\":\"input\"}}");

            // update presentationRequest from manifest with things that don't change for each request
            if (!config["authority"].ToString().StartsWith("did:ion:"))
            {
                config["authority"] = manifest["input"]["issuer"];
            }
            config["registration"]["clientName"] = "DotNet Client API Verifier"; // AppSettings.client_name;

            var requestedCredentials = config["presentation"]["requestedCredentials"][0];
            if (requestedCredentials["type"].ToString().Length == 0)
            {
                requestedCredentials["type"] = manifest["id"];
            }
            requestedCredentials["trustedIssuers"][0] = manifest["input"]["issuer"]; //VCSettings.didIssuer;

            json = JsonConvert.SerializeObject(config);

            CacheValueWithNoExpiration("presentationRequest", json);
            return await Task.FromResult(config);
        }
        */

        protected JObject GetPresentationManifest()
        {
            if (GetCachedValue("manifestPresentation", out string json))
            {
                return JObject.Parse(json);
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
