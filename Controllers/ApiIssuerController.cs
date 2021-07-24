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
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace AA.DIDApi.Controllers
{
    [Route("api/issuer")]
    [ApiController]
    public class ApiIssuerController : ApiBaseVCController
    {
        private readonly ILogger _logger;

        private const string IssuanceRequestConfigFile = "issuance_request_accessamerica.json";

        public ApiIssuerController(
            IConfiguration configuration,
            IOptions<AppSettingsModel> appSettings,
            IMemoryCache memoryCache,
            IWebHostEnvironment env,
            //ILogger<ApiIssuerController> log,
            ILoggerFactory loggerFactory) 
                : base(configuration, appSettings, memoryCache, env//, log
                                                                   )
        {
            _logger = loggerFactory.CreateLogger("IssuerLogger");

            GetIssuanceRequest().GetAwaiter().GetResult();
        }

        #region Endpoints

        [HttpGet("echo")]
        public async Task<ActionResult> Echo()
        {
            TraceHttpRequest();
         
            try 
            {
                JObject config = await GetIssuanceRequest();
                JObject manifest = GetIssuanceManifest();
                var info = new
                {
                    date = DateTime.Now.ToString(),
                    host = GetRequestHostName(),
                    api = GetApiPath(),
                    didIssuer = manifest["input"]["issuer"], 
                    credentialType = manifest["id"], 
                    displayCard = manifest["display"]["card"],
                    buttonColor = "#000080",
                    contract = manifest["display"]["contract"],
                    selfAssertedClaims = config["issuance"]["claims"]
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

            JObject manifest = GetIssuanceManifest();
            return Redirect(manifest["display"]["card"]["logo"]["uri"].ToString());
        }

        [HttpGet("issue-request")]
        public async Task<ActionResult> GetIssuanceReference()
        {
            TraceHttpRequest();

            try 
            {
                JObject issuanceRequest = await GetIssuanceRequest();
                if (issuanceRequest == null) 
                {
                    return ReturnErrorMessage("Issuance Request Config File not found");
                }
                
                // set self-asserted claims passed as query string parameters
                if (((JObject)issuanceRequest["issuance"]).ContainsKey("claims"))
                {
                    foreach (var c in (JObject)issuanceRequest["issuance"]["claims"]) 
                    {
                        issuanceRequest["issuance"]["claims"][c.Key] = this.Request.Query[c.Key].ToString();
                    }
                }

                string correlationId = Guid.NewGuid().ToString();
                issuanceRequest["callback"]["url"] = $"{GetApiPath()}/issuanceCallback";
                issuanceRequest["callback"]["state"] = correlationId;
                issuanceRequest["callback"]["nonce"] = Guid.NewGuid().ToString();
                issuanceRequest["callback"]["headers"]["my-api-key"] = this.AppSettings.ApiKey;
                string pin = null;

                int pinLength = 0;
                if (((JObject)issuanceRequest["issuance"]).ContainsKey("pin"))
                {
                    int.TryParse(issuanceRequest["issuance"]["pin"]["length"].ToString(), out pinLength);
                    if (pinLength > 0)
                    {
                        int pinMaxValue = int.Parse("".PadRight(pinLength, '9'));          // 9999999
                        int randomNumber = RandomNumberGenerator.GetInt32(1, pinMaxValue);
                        pin = string.Format("{0:D" + pinLength.ToString() + "}", randomNumber);
                        _logger.LogInformation($"pin={pin}");
                        issuanceRequest["issuance"]["pin"]["value"] = pin;
                    }
                }

                string jsonString = JsonConvert.SerializeObject(issuanceRequest);
                HttpActionResponse httpPostResponse = await HttpPostAsync(jsonString);
                if (!httpPostResponse.IsSuccessStatusCode)
                {
                    _logger.LogError($"VC Client API Error Response\n{httpPostResponse.ResponseContent}\n{httpPostResponse.StatusCode}");
                    return ReturnErrorMessage(httpPostResponse.ResponseContent);
                }
                
                JObject requestConfig = JObject.Parse(httpPostResponse.ResponseContent);
                if(pinLength > 0)
                {
                    requestConfig["pin"] = pin;
                }

                requestConfig.Add(new JProperty("id", correlationId));
                jsonString = JsonConvert.SerializeObject(requestConfig);
                _logger.LogInformation($"VC Client API Response\n{jsonString}");
            
                return ReturnJson(jsonString);
            }  
            catch (Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost("issuanceCallback")]
        public async Task<ActionResult> PostIssuanceCallback()
        {
            TraceHttpRequest();

            try 
            {
                _logger.LogInformation("issuanceCallback");
                string body = await GetRequestBodyAsync();
                _logger.LogInformation(body);
                JObject issuanceResponse = JObject.Parse(body);
            
                if (issuanceResponse["code"].ToString() == "request_retrieved")
                {
                    string correlationId = issuanceResponse["state"].ToString();
                    var cacheData = new 
                    {
                        status = 1,
                        message = "QR Code is scanned. Waiting for issuance to complete."
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

        [HttpPost("response")]
        public async Task<ActionResult> PostResponse()
        {
            TraceHttpRequest();

            try 
            {
                _logger.LogInformation("response");
                string body = await GetRequestBodyAsync();
                JObject claims = JObject.Parse(body);
                CacheJsonObjectWithExpiration(claims["state"].ToString(), claims);
                return new OkResult();
            }
            catch(Exception ex) 
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet("issue-response")]
        public ActionResult GetIssuanceResponse()
        {
            TraceHttpRequest();

            try 
            {
                _logger.LogInformation($"[api/issuer/issue-response] Request.Query.Keys={Request.Query.Keys.Count}");
                foreach (string key in Request.Query.Keys)
                {
                    _logger.LogInformation($"Key={key}, value={Request.Query[key]}");
                }

                if (!Request.Query.ContainsKey("id") || string.IsNullOrEmpty(Request.Query["id"]))
                {
                    return ReturnErrorMessage("Missing argument 'id'");
                }

                string correlationId = Request.Query["id"];
                if(GetCachedValue(correlationId, out string body))
                {
                    RemoveCacheValue(correlationId);
                    return ReturnJson(body);
                }

                return new OkResult();
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
            return $"{GetRequestHostName()}/api/issuer";
        }

        protected async Task<JObject> GetIssuanceRequest()
        {
            if (GetCachedValue("issuanceRequest", out string json))
            {
                return JObject.Parse(json);
            }

            // see if file path was passed on command line
            string issuanceRequestFile = _configuration.GetValue<string>("IssuanceRequestConfigFile");
            if (string.IsNullOrEmpty(issuanceRequestFile))
            {
                issuanceRequestFile = IssuanceRequestConfigFile;
            }

            string fileLocation = Directory.GetParent(typeof(Program).Assembly.Location).FullName;
            string file = $"{fileLocation}\\{issuanceRequestFile}";
            if (!System.IO.File.Exists(file))
            {
                _logger.LogError($"File not found: {issuanceRequestFile}");
                return null;
            }

            _logger.LogInformation($"IssuanceRequest file: {issuanceRequestFile}");
            json = System.IO.File.ReadAllText(file);
            JObject config = JObject.Parse(json);

            // download manifest and cache it
            HttpActionResponse httpGetResponse = await HttpGetAsync(config["issuance"]["manifest"].ToString());
            if (!httpGetResponse.IsSuccessStatusCode)
            {
                _logger.LogError($"HttpStatus {httpGetResponse.StatusCode} fetching manifest {config["issuance"]["manifest"]}");
                return null;
            }

            CacheValueWithNoExpiration("manifestIssuance", httpGetResponse.ResponseContent);
            JObject manifest = JObject.Parse(httpGetResponse.ResponseContent);

            // update presentationRequest from manifest with things that don't change for each request
            if (!config["authority"].ToString().StartsWith("did:ion:"))
            {
                config["authority"] = manifest["input"]["issuer"];
            }
            if (config["issuance"]["type"].ToString().Length == 0)
            {
                config["issuance"]["type"] = manifest["id"];
            }
            config["registration"]["clientName"] = AppSettings.client_name;

            // if we have pin code but length is zero, remove it since VC Client API will give error then
            if (((JObject)config["issuance"]).ContainsKey("pin"))
            {
                if (int.TryParse(config["issuance"]["pin"]["length"].ToString(), out int pinLength))
                {
                    if (pinLength == 0)
                    {
                        (config["issuance"] as JObject).Remove("pin");
                    }
                }
            }

            json = JsonConvert.SerializeObject(config);
            CacheValueWithNoExpiration("issuanceRequest", json);
            return config;
        }

        protected JObject GetIssuanceManifest()
        {
            if (GetCachedValue("manifestIssuance", out string json))
            {
                return JObject.Parse(json);
            }

            return null;
        }

        #endregion Endpoints
    }
}
