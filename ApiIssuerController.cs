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
    [Route("api/issuer/[action]")]
    [ApiController]
    public class ApiIssuerController : ApiBaseVCController
    {
        private const string IssuanceRequestConfigFile = "issuance_request_config.json";

        public ApiIssuerController(IOptions<AppSettingsModelVC> vcSettings, IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, IWebHostEnvironment env, ILogger<ApiIssuerController> log) : base(vcSettings, appSettings, memoryCache, env, log)
        {
        }

        protected string GetApiPath() {
            return string.Format("{0}/api/issuer", GetRequestHostName());
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// REST APIs
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        [HttpGet]
        public async Task<ActionResult> echo() {
            TraceHttpRequest();
            try {
                JObject manifest = JObject.Parse(GetDidManifest());
                var info = new
                {
                    date = DateTime.Now.ToString(),
                    host = GetRequestHostName(),
                    api = GetApiPath(),
                    didIssuer = VCSettings.didIssuer,
                    credentialType = VCSettings.credentialType,
                    client_purpose = VCSettings.client_purpose,
                    displayCard = manifest["display"]["card"],
                    buttonColor = "#000080",
                    contract = manifest["display"]["contract"],
                    selfAssertedClaims = this.VCSettings.selfAssertedClaims
                };
                return ReturnJson(JsonConvert.SerializeObject(info));
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet]
        public async Task<ActionResult> issuance() {
            TraceHttpRequest();
            try {
                return SendStaticJsonFile(IssuanceRequestConfigFile);
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }
        [HttpGet("/api/issuer/issue-request")]
        public async Task<ActionResult> issuanceReference() {
            TraceHttpRequest();
            try {
                string jsonString = ReadFile(IssuanceRequestConfigFile);
                if ( string.IsNullOrEmpty(jsonString) ) {
                    return ReturnErrorMessage( IssuanceRequestConfigFile + " not found" );
                }
                // get self asserted claims, if any
                JObject selfAssertedClaims = null;
                if (this.VCSettings.selfAssertedClaims != null && this.VCSettings.selfAssertedClaims.Count > 0) {
                    selfAssertedClaims = new JObject();
                    foreach (var c in this.VCSettings.selfAssertedClaims) {
                        selfAssertedClaims.Add( new JProperty(c.claim, this.Request.Query[c.claim].ToString() ));
                    }
                }                
                string state = Guid.NewGuid().ToString();
                string nonce = Guid.NewGuid().ToString();
                JObject config = JObject.Parse(jsonString);
                config["authority"] = VCSettings.didIssuer;
                config["registration"]["clientName"] = AppSettings.client_name;
                //string urlCallback = string.Format("{0}/issuanceCallback/{1}", GetApiPath(), pin);
                string urlCallback = string.Format("{0}/issuanceCallback", GetApiPath() );
                config["issuance"]["callback"] = urlCallback;
                config["issuance"]["state"] = state;
                config["issuance"]["nonce"] = nonce;
                config["issuance"]["type"] = VCSettings.credentialType;
                config["issuance"]["manifest"] = VCSettings.manifest;
                string pin = null;
                if (this.VCSettings.PinCodeLength > 0 ) {
                    int pinMaxValue = int.Parse("".PadRight(this.VCSettings.PinCodeLength, '9'));          // 9999999
                    int randomNumber = RandomNumberGenerator.GetInt32(1, pinMaxValue);
                    pin = string.Format("{0:D" + this.VCSettings.PinCodeLength.ToString() + "}", randomNumber);
                    _log.LogTrace("pin={0}", pin);
                    config["issuance"]["pin"]["value"] = pin;
                    config["issuance"]["pin"]["length"] = this.VCSettings.PinCodeLength;
                } else {
                    (config["issuance"] as JObject).Remove("pin");
                }
                if ( selfAssertedClaims != null ) {
                    config["issuance"]["claims"] = selfAssertedClaims;
                } else {
                    (config["issuance"] as JObject).Remove("claims");
                }
                jsonString = JsonConvert.SerializeObject(config);
                _log.LogTrace(jsonString);
                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out contents) ) {
                    return ReturnErrorMessage( contents );
                }
                JObject requestConfig = JObject.Parse(contents);
                if (this.VCSettings.PinCodeLength > 0) {
                    requestConfig["pin"] = pin;
                }
                //  iOS Authenticator doesn't allow redirects - if you set UsaAkaMS == true in appsettings.json, you don't need this
                requestConfig["url"] = requestConfig["url"].ToString().Replace("https://aka.ms/vcrequest?", "https://draft.azure-api.net/api/client/v1.0/request?");
                requestConfig.Add(new JProperty("id", state));
                requestConfig.Add(new JProperty("link", requestConfig["url"].ToString()));
                jsonString = JsonConvert.SerializeObject(requestConfig);
                return ReturnJson( jsonString );
            }  catch (Exception ex)  {
                return ReturnErrorMessage( ex.Message );
            }
        }

        [HttpPost]
        public async Task<ActionResult> issuanceCallback() {
            TraceHttpRequest();
            try {
                _log.LogTrace("issuanceCallback");
                string body = GetRequestBody();
                _log.LogTrace(body);
                JObject issuanceResponse = JObject.Parse(body);
                if (issuanceResponse["message"].ToString() == "request_retrieved") {
                    string requestId = issuanceResponse["requestId"].ToString();
                    var cacheData = new {
                        status = 1,
                        message = "QR Code is scanned. Waiting for issuance to complete.",
                        vc = ""
                    };
                    CacheJsonObject(requestId, cacheData);
                }
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult> response() {
            TraceHttpRequest();
            try {
                _log.LogTrace("response");
                string body = GetRequestBody();
                JObject claims = JObject.Parse(body);
                CacheJsonObject(claims["state"].ToString(), claims);
                return new OkResult();
            } catch( Exception ex ) {
                return ReturnErrorMessage( ex.Message );
            }
        }

        [HttpGet("/api/issuer/issue-response")]
        public async Task<ActionResult> issuanceResponse() {
            TraceHttpRequest();
            try {
                string state = this.Request.Query["id"];
                if (string.IsNullOrEmpty(state)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                string body = null;
                if ( GetCachedValue(state, out body)) {
                    RemoveCacheValue(state);
                    return ReturnJson(body);
                } else {
                    string requestId = this.Request.Query["requestId"];
                    if (!string.IsNullOrEmpty(requestId) && _cache.TryGetValue(requestId, out body)) {
                        RemoveCacheValue(requestId);
                        return ReturnJson(body);
                    }
                }
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

    } // cls
} // ns
