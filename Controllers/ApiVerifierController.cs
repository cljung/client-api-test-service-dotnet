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
        private readonly ILogger _logger;

        protected const string PresentationRequestConfigFile = "presentation_request_accessamerica.json";

        public ApiVerifierController(
            IConfiguration configuration,
            IOptions<AppSettingsModel> appSettings,
            IMemoryCache memoryCache,
            IWebHostEnvironment env,
            //ILogger<ApiVerifierController> log,
            ILoggerFactory loggerFactory)
                : base(configuration, appSettings, memoryCache, env//, log
                                                                   )
        {
            _logger = loggerFactory.CreateLogger("VerifierLogger");

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
                string fileLocation = string.Empty;
                string exception = string.Empty;
                try
                {
                    var result = await GetPresentationRequest();
                    presentationRequest = result;
                }
                catch (Exception ex)
                {
                    exception = ex.Message;
                }
                if (presentationRequest == null)
                {
                    return ReturnErrorMessage($"Presentation Request Config File not found: file: ex:'{exception}'");
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
                callback["nonce"] = Guid.NewGuid().ToString();
                callback["headers"]["my-api-key"] = this.AppSettings.ApiKey;
                
                string jsonString = JsonConvert.SerializeObject(presentationRequest);
                HttpActionResponse httpPostResponse = await HttpPostAsync(jsonString);
                if (!httpPostResponse.IsSuccessStatusCode)
                {
                    string message = $"VC Client API Error Response\n{httpPostResponse.ResponseContent}\nStatus:{httpPostResponse.StatusCode}\n posting to: {this.AppSettings.ApiEndpoint}\n with body: {jsonString}";
                    _logger.LogError(message);
                    return ReturnErrorMessage(message);
                }

                // pass the response to our caller (but add id)
                JObject apiResp = JObject.Parse(httpPostResponse.ResponseContent);
                apiResp.Add(new JProperty("id", correlationId));
                httpPostResponse.ResponseContent = JsonConvert.SerializeObject(apiResp);

                _logger.LogTrace($"VC Client API Response\n{httpPostResponse.ResponseContent}");
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
                _logger.LogTrace(body);
                JObject presentationResponse = JObject.Parse(body);
                string correlationId = presentationResponse["state"].ToString();

                string presentationResponseCode = presentationResponse["code"]?.ToString();
                if (string.IsNullOrEmpty(presentationResponseCode) ||
                    (presentationResponseCode != "request_retrieved" &&
                    presentationResponseCode != "presentation_verified"))
                {
                    _logger.LogError($"presentationCallback() - presentationResponse[\"code\"] = {presentationResponseCode}");
                    return new BadRequestResult();
                }

                // request_retrieved == QR code has been scanned and request retrieved from VC Client API
                if (presentationResponse["code"].ToString() == "request_retrieved")
                {
                    _logger.LogTrace("presentationCallback() - request_retrieved");
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
                    _logger.LogTrace($"presentationCallback() - presentation_verified\n{claims}");

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
                    _logger.LogTrace($"status={cacheData["status"]}, message={cacheData["message"]}");
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

        //[HttpPost("presentation-response-b2c")]
        //public async Task<ActionResult> PostPresentationResponseB2C()
        //{
        //    TraceHttpRequest();

        //    try
        //    {
        //        string body = await GetRequestBodyAsync();
        //        _log.LogTrace(body);
        //        JObject b2cRequest = JObject.Parse(body);
        //        string correlationId = b2cRequest["id"].ToString();
        //        if (string.IsNullOrEmpty(correlationId))
        //        {
        //            return ReturnErrorMessage("Missing argument 'id'");
        //        }

        //        if (!GetCachedJsonObject(correlationId, out JObject cacheData))
        //        {
        //            return ReturnErrorB2C("Verifiable Credentials not presented"); // 409
        //        }

        //        // remove cache data now, because if we crash, we don't want to get into an infinite loop of crashing 
        //        RemoveCacheValue(correlationId);

        //        // get the payload from the presentation-response callback
        //        var presentationResponse = cacheData["presentationResponse"];

        //        // get the claims tha the VC Client API provides to us from the presented VC
        //        JObject vcClaims = (JObject)presentationResponse["issuers"][0]["claims"];

        //        // get the token that was presented and dig out the VC credential from it since we want to return the
        //        // Issuer DID and the holders DID to B2C
        //        JObject didIdToken = JWTTokenToJObject(presentationResponse["receipt"]["id_token"].ToString());
        //        var credentialType = didIdToken["presentation_submission"]["descriptor_map"][0]["id"].ToString();
        //        var presentationPath = didIdToken["presentation_submission"]["descriptor_map"][0]["path"].ToString();

        //        JObject presentation = JWTTokenToJObject(didIdToken.SelectToken(presentationPath).ToString());
        //        string vcToken = presentation["vp"]["verifiableCredential"][0].ToString();

        //        JObject vc = JWTTokenToJObject(vcToken);
        //        string displayName = vcClaims.ContainsKey("displayName")
        //            ? vcClaims["displayName"].ToString()
        //            : $"{vcClaims["firstName"]} {vcClaims["lastName"]}";

        //        // these claims are optional
        //        string sub = null;
        //        string tid = null;
        //        string username = null;

        //        if (vcClaims.ContainsKey("tid"))
        //        {
        //            tid = vcClaims["tid"].ToString();
        //        }
        //        if (vcClaims.ContainsKey("sub"))
        //        { 
        //            sub = vcClaims["sub"].ToString();
        //        }
        //        if (vcClaims.ContainsKey("username"))
        //        { 
        //            username = vcClaims["username"].ToString();
        //        }

        //        var b2cResponse = new
        //        {
        //            id = correlationId,
        //            credentialsVerified = true,
        //            credentialType = credentialType,
        //            displayName = displayName,
        //            givenName = vcClaims["firstName"].ToString(),
        //            surName = vcClaims["lastName"].ToString(),
        //            iss = vc["iss"].ToString(),
        //            sub = vc["sub"].ToString(),
        //            key = vc["sub"].ToString().Replace("did:ion:","did.ion.").Split(":")[0],
        //            oid = sub,
        //            tid = tid,
        //            username = username
        //        };
        //        string resp = JsonConvert.SerializeObject(b2cResponse);
        //        _log.LogTrace(resp);

        //        return ReturnJson(resp);
        //    }
        //    catch (Exception ex)
        //    {
        //        return ReturnErrorMessage(ex.Message);
        //    }
        //}

        [HttpPost("presentation-response-b2c")]
        public async Task<ActionResult> PostPresentationResponseB2C()
        {
            TraceHttpRequest();

            try
            {
                string body;
                try
                {
                    body = await GetRequestBodyAsync();
                    _logger.LogTrace(body);
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing body. error={ex.Message}");
                }

                JObject b2cRequest;
                try
                {
                    b2cRequest = JObject.Parse(body);
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing json body. body={body} error={ex.Message}");
                }

                string correlationId;
                try
                {
                    correlationId = b2cRequest["id"].ToString();
                    if (string.IsNullOrEmpty(correlationId))
                    {
                        return ReturnErrorMessage("Missing argument 'id'");
                    }
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing correlationId from b2cRequest={b2cRequest}. error={ex.Message}");
                }

                if (!GetCachedJsonObject(correlationId, out JObject cacheData))
                {
                    return ReturnErrorB2C("Verifiable Credentials not presented"); // 409
                }

                // remove cache data now, because if we crash, we don't want to get into an infinite loop of crashing 
                RemoveCacheValue(correlationId);

                // get the payload from the presentation-response callback
                JToken presentationResponse;
                try
                {
                    presentationResponse = cacheData["presentationResponse"];
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing presentationResponse from cache. CI={correlationId} b2cRequest={b2cRequest} cacheData={cacheData} error={ex.Message}");
                }

                // get the claims tha the VC Client API provides to us from the presented VC
                JObject vcClaims;
                try
                {
                    var dynamic = presentationResponse["issuers"][0]["claims"];
                    vcClaims = (JObject)dynamic;
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing vcClaims from presentationResponse. CI={correlationId} presentationResponse={presentationResponse} error={ex.Message}");
                }

                // get the token that was presented and dig out the VC credential from it since we want to return the
                // Issuer DID and the holders DID to B2C
                JObject didIdToken;
                try
                {
                    didIdToken = JWTTokenToJObject(presentationResponse["receipt"]["id_token"].ToString());
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing didIdToken from presentationResponse. presentationResponse={presentationResponse} error={ex.Message}");
                }

                string credentialType;
                try
                {
                    credentialType = didIdToken["presentation_submission"]["descriptor_map"][0]["id"].ToString();
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing credentialType from didIdToken. didIdToken={didIdToken} error={ex.Message}");
                }

                string presentationPath;
                try
                {
                    presentationPath = didIdToken["presentation_submission"]["descriptor_map"][0]["path"].ToString();
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing presentationPath from didIdToken. didIdToken={didIdToken} error={ex.Message}");
                }

                JObject presentation;
                try
                {
                    presentation = JWTTokenToJObject(didIdToken.SelectToken(presentationPath).ToString());
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing presentation from didIdToken. didIdToken={didIdToken} error={ex.Message}");
                }

                string vcToken;
                try
                {
                    vcToken = presentation["vp"]["verifiableCredential"][0].ToString();
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing vcToken from didIdToken. didIdToken={didIdToken} error={ex.Message}");
                }

                JObject vc;
                try
                {
                    vc = JWTTokenToJObject(vcToken);
                }
                catch (Exception ex)
                {
                    return ReturnErrorMessage($"Error parsing vc object from vcToken. vcToken={vcToken} error={ex.Message}");
                }

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
                    key = vc["sub"].ToString().Replace("did:ion:", "did.ion.").Split(":")[0],
                    oid = sub,
                    tid = tid,
                    username = username
                };
                string resp = JsonConvert.SerializeObject(b2cResponse);
                _logger.LogTrace(resp);

                return ReturnJson(resp);
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Generic error: {ex.Message}");
            }
        }

        /*
        /// <summary>
        /// Temp implementation
        /// </summary>
        [HttpPost("presentation-response-b2c")]
        public async Task<ActionResult> PostPresentationResponseB2C()
        {
            TraceHttpRequest();

            string body;
            try
            {
                body = await GetRequestBodyAsync();
                _log.LogTrace(body);
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing bod. error={ex.Message}");
            }

            JObject b2cRequest;
            try
            {
                b2cRequest = JObject.Parse(body);  
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing json body. error={ex.Message}");
            }

            string correlationId;
            try
            {
                correlationId = b2cRequest["id"].ToString();
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing correlationId. error={ex.Message}");
            }

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
            JToken presentationResponse;
            try
            {
                presentationResponse = cacheData["presentationResponse"];
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing presentationResponse from cache. b2cRequest={b2cRequest} cacheData={cacheData} error={ex.Message}");
            }

            // get the claims tha the VC Client API provides to us from the presented VC
            JObject vcClaims;
            try
            {
                vcClaims = (JObject)presentationResponse["issuers"][0]["claims"];
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing vcClaims from presentationResponse. presentationResponse={presentationResponse} error={ex.Message}");
            }

            // get the token that was presented and dig out the VC credential from it since we want to return the
            // Issuer DID and the holders DID to B2C
            JObject didIdToken;
            try
            {
                didIdToken = JWTTokenToJObject(presentationResponse["receipt"]["id_token"].ToString());
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing didIdToken from presentationResponse. presentationResponse={presentationResponse} error={ex.Message}");
            }

            string credentialType;
            try
            {
                credentialType = didIdToken["presentation_submission"]["descriptor_map"][0]["id"].ToString();
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing credentialType from didIdToken. didIdToken={didIdToken} error={ex.Message}");
            }

            string presentationPath;
            try
            {
                presentationPath = didIdToken["presentation_submission"]["descriptor_map"][0]["path"].ToString();
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing presentationPath from didIdToken. didIdToken={didIdToken} error={ex.Message}");
            }

            JObject presentation;
            try
            {
                presentation = JWTTokenToJObject(didIdToken.SelectToken(presentationPath).ToString());
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing presentation from didIdToken. didIdToken={didIdToken} error={ex.Message}");
            }

            string vcToken;
            try
            {
                vcToken = presentation["vp"]["verifiableCredential"][0].ToString();
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing vcToken from didIdToken. didIdToken={didIdToken} error={ex.Message}");
            }

            JObject vc;
            try
            {
                vc = JWTTokenToJObject(vcToken);
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing vc object from vcToken. vcToken={vcToken} error={ex.Message}");
            }

            string displayName;
            try
            {
                displayName = vcClaims.ContainsKey("displayName")
                            ? vcClaims["displayName"].ToString()
                            : $"{vcClaims["firstName"]} {vcClaims["lastName"]}";
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage($"Error parsing vc displayName from vcClaims. vcClaims={vcClaims} error={ex.Message}");
            }

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
                key = vc["sub"].ToString().Replace("did:ion:", "did.ion.").Split(":")[0],
                oid = sub,
                tid = tid,
                username = username
            };

            string resp = JsonConvert.SerializeObject(b2cResponse);
            _log.LogTrace(resp);

            return ReturnJson(resp);

        }
        */

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
                _logger.LogInformation($"presentationRequest retrieved from Cache: {json}");
                return JObject.Parse(json);
            }

            // see if file path was passed on command line
            string presentationRequestFile = _configuration.GetValue<string>("PresentationRequestConfigFile");
            if (string.IsNullOrEmpty(presentationRequestFile))
            {
                presentationRequestFile = PresentationRequestConfigFile;
            }

            string fileLocation = Directory.GetParent(typeof(Program).Assembly.Location).FullName;
            string file = presentationRequestFile.StartsWith("requests")
                ? $"{fileLocation}\\{presentationRequestFile}"
                : $"{fileLocation}\\requests\\{presentationRequestFile}";
            if (!System.IO.File.Exists(file))
            {
                _logger.LogError($"File not found: {presentationRequestFile}");
                return null;
            }

            json = System.IO.File.ReadAllText(file);
            //json = "{\"authority\": \"did:ion:EiD_ZULUvK_3eTfWfq97cc87DeU8J0AEzIaSuFLRAHzXoQ:eyJkZWx0YSI6eyJwYXRjaGVzIjpbeyJhY3Rpb24iOiJyZXBsYWNlIiwiZG9jdW1lbnQiOnsicHVibGljS2V5cyI6W3siaWQiOiJzaWdfY2FiNjVhYTAiLCJwdWJsaWNLZXlKd2siOnsiY3J2Ijoic2VjcDI1NmsxIiwia3R5IjoiRUMiLCJ4IjoiU21xMVZNNXp0RUVpZGpoWE5uckxub3N5TkI2MEVaV05CWXdUY3dQazU3YyIsInkiOiJSeFd3QlNyQjBaWl9MdndKVGpMamRqUEtTMXlZQjgzOUZIckFWUm9EYW9nIn0sInB1cnBvc2VzIjpbImF1dGhlbnRpY2F0aW9uIiwiYXNzZXJ0aW9uTWV0aG9kIl0sInR5cGUiOiJFY2RzYVNlY3AyNTZrMVZlcmlmaWNhdGlvbktleTIwMTkifV0sInNlcnZpY2VzIjpbeyJpZCI6ImxpbmtlZGRvbWFpbnMiLCJzZXJ2aWNlRW5kcG9pbnQiOnsib3JpZ2lucyI6WyJodHRwczovL3ZjYXBpZGV2Lndvb2Rncm92ZWRlbW8uY29tLyJdfSwidHlwZSI6IkxpbmtlZERvbWFpbnMifV19fV0sInVwZGF0ZUNvbW1pdG1lbnQiOiJFaUIza0VSRUlwSWdodFp2SUxaemFlMDF1NGtXSVNnWHFqN0lFUFVfUGNBUnJBIn0sInN1ZmZpeERhdGEiOnsiZGVsdGFIYXNoIjoiRWlEcGMtelNRcHJYMGhacDdlMC1QXzhwWjkzdm9EZXhwLVo1ZXVtbFhzN2hQUSIsInJlY292ZXJ5Q29tbWl0bWVudCI6IkVpQWtKOTNBLThyeXF3X0VqSVRuSEpqdkhvZ016N2YtTlRZWXlhOENVMEdYdWcifX0\",\"includeQRCode\": false,\"registration\": {\"clientName\": \"...set at runtime...\"},\"callback\": {\"url\": \"...set at runtime...\",\"nonce\": \"...set at runtime...\",\"state\": \"...set at runtime...\",\"headers\": {\"my-api-key\": \"blabla\"}},\"presentation\": {\"includeReceipt\": true,\"requestedCredentials\": [{\"type\": \"\",\"manifest\": \"https://beta.did.msidentity.com/v1.0/3c32ed40-8a10-465b-8ba4-0b1e86882668/verifiableCredential/contracts/VerifiedCredentialExpert\",\"purpose\": \"the purpose why the verifier asks for a VC\",\"trustedIssuers\": [ \"did-of-the-Issuer-trusted\" ]}]}}";
            JObject config = JObject.Parse(json);

            // download manifest and cache it
            HttpActionResponse httpGetResponse = await HttpGetAsync(config["presentation"]["requestedCredentials"][0]["manifest"].ToString());
            if (!httpGetResponse.IsSuccessStatusCode)
            {
                _logger.LogError($"HttpStatus {httpGetResponse.StatusCode} fetching manifest {config["presentation"]["requestedCredentials"][0]["manifest"]}");
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
