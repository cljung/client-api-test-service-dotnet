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

namespace client_api_test_service_dotnet
{
    [Route("api/issuer/[action]")]
    [ApiController]
    public class ApiIssuerController : ControllerBase
    {
        private IMemoryCache _cache;
        private readonly IHostingEnvironment _env;
        private readonly AppSettingsModel AppSettings;

        private const string IssuanceRequestConfigFile = "issuance_request_config.json";

        public ApiIssuerController(IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, IHostingEnvironment env)
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _env = env;
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Helpers
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        private string GetRequestHostName()
        {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty(originalHost))
                 hostname = string.Format("{0}://{1}", scheme, originalHost);
            else hostname = string.Format("{0}://{1}", scheme, this.Request.Host);
            return hostname;
        }
        private string GetApiPath()
        {
            return string.Format("{0}/api/issuer", GetRequestHostName());
        }

        // return 400 error-message
        private ActionResult ReturnErrorMessage(string errorMessage)
        {
            return BadRequest(new { error = "400", error_description = errorMessage });
        }
        // return 200 json 
        private ActionResult ReturnJson( string json )
        {
            return new ContentResult { ContentType = "application/json", Content = json };
        }
        // read & cache the file
        private string ReadFile(string filename)
        {
            string json = null;
            string path = Path.Combine(_env.WebRootPath, filename);
            if (!_cache.TryGetValue(path, out json)) {
                if (System.IO.File.Exists(path)) {
                    json = System.IO.File.ReadAllText(path);
                    _cache.Set(path, json);
                }
            }
            return json;
        }
        // read and return a file
        private ActionResult SendStaticJsonFile(string filename)
        {
            string json = ReadFile(filename);
            if (!string.IsNullOrEmpty(json)) {
                return ReturnJson( json );
            } else {
                return ReturnErrorMessage( filename + " not found" );
            }
        }

        // set a cookie
        private bool HttpPost(string body, out HttpStatusCode statusCode, out string response)
        {
            response = null;
            HttpClient client = new HttpClient();
            client = new HttpClient();
            client.DefaultRequestHeaders.Add("x-ms-functions-key", this.AppSettings.ApiKey);
            HttpResponseMessage res = client.PostAsync(this.AppSettings.ApiEndpoint, new StringContent(body, Encoding.UTF8, "application/json")).Result;
            response = res.Content.ReadAsStringAsync().Result;
            client.Dispose();
            statusCode = res.StatusCode;
            return res.IsSuccessStatusCode;
        }
        private bool HttpGet(string url, out HttpStatusCode statusCode, out string response)
        {
            response = null;
            HttpClient client = new HttpClient();
            client = new HttpClient();
            HttpResponseMessage res = client.GetAsync(url).Result;
            response = res.Content.ReadAsStringAsync().Result;
            client.Dispose();
            statusCode = res.StatusCode;
            return res.IsSuccessStatusCode;
        }

        private string GetRequestBody()
        {
            string body = null;
            using (var reader = new System.IO.StreamReader(this.Request.Body)) {
                body = reader.ReadToEnd();
            }
            return body;
        }

        private string GetDidManifest()
        {
            string contents = null;
            if (!_cache.TryGetValue("manifest", out contents))
            {
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if (HttpGet(AppSettings.manifest, out statusCode, out contents))
                {
                    _cache.Set("manifest", contents);
                }
            }
            return contents;
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// REST APIs
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        [HttpGet]
        public async Task<ActionResult> echo()
        {
            try
            {
                JObject manifest = JObject.Parse(GetDidManifest());
                var info = new
                {
                    date = DateTime.Now.ToString(),
                    host = GetRequestHostName(),
                    api = GetApiPath(),
                    didIssuer = AppSettings.didIssuer,
                    credentialType = AppSettings.credentialType,
                    client_purpose = AppSettings.client_purpose,
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
        public async Task<ActionResult> issuance()
        {
            try{
                return SendStaticJsonFile(IssuanceRequestConfigFile);
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }
        [HttpGet]
        public async Task<ActionResult> issuanceReference()
        {
            try {
                string jsonString = ReadFile(IssuanceRequestConfigFile);
                if ( string.IsNullOrEmpty(jsonString) ) {
                    return ReturnErrorMessage( IssuanceRequestConfigFile + " not found" );
                }

                string state = Guid.NewGuid().ToString();
                string nonce = Guid.NewGuid().ToString();
                int pinMaxValue = int.Parse("".PadRight(this.AppSettings.PinCodeLength, '9'));          // 9999999
                int randomNumber = RandomNumberGenerator.GetInt32(1, pinMaxValue);
                string pin = string.Format("{0:D" + this.AppSettings.PinCodeLength.ToString() + "}", randomNumber);

                JObject config = JObject.Parse(jsonString);

                config["authority"] = AppSettings.didIssuer;
                config["registration"]["clientName"] = AppSettings.client_name;
                config["registration"]["logoUrl"] = AppSettings.client_logo_uri;
                string urlCallback = string.Format("{0}/issuanceCallback/{1}", GetApiPath(), pin);
                config["issuance"]["callback"] = urlCallback;
                config["issuance"]["state"] = state;
                config["issuance"]["nonce"] = nonce;
                config["issuance"]["type"] = AppSettings.credentialType;
                config["issuance"]["manifest"] = AppSettings.manifest;
                config["issuance"]["pin"]["value"] = pin;

                jsonString = JsonConvert.SerializeObject(config);
                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out contents) ) {
                    return ReturnErrorMessage( contents );
                }
                JObject requestConfig = JObject.Parse(contents);
                requestConfig["pin"] = pin;
                requestConfig["url"] = requestConfig["url"].ToString().Replace("https://aka.ms/vcrequest?", "https://draft.azure-api.net/api/client/v1.0/request?");
                requestConfig.Add(new JProperty("id", state));
                jsonString = JsonConvert.SerializeObject(requestConfig);
                return ReturnJson( jsonString );
            }  catch (Exception ex)  {
                return ReturnErrorMessage( ex.Message );
            }
        }

        [HttpPost]
        public async Task<ActionResult> issuanceCallback()
        {
            try
            {
                Console.WriteLine("issuanceCallback");
                string body = GetRequestBody();
                JObject issuanceResponse = JObject.Parse(body);
                if (issuanceResponse["message"].ToString() == "request_retrieved")
                {
                    string requestId = issuanceResponse["requestId"].ToString();
                    var cacheData = new
                    {
                        status = 1,
                        message = "QR Code is scanned. Waiting for issuance to complete.",
                        vc = ""
                    };
                    _cache.Set(requestId, JsonConvert.SerializeObject(cacheData));
                }
                return new OkResult();
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult> response()
        {
            try {
                Console.WriteLine("response");
                string body = GetRequestBody();
                JObject claims = JObject.Parse(body);
                string state = claims["state"].ToString();
                _cache.Set(state, body);
                return new OkResult();
            } catch( Exception ex ) {
                return ReturnErrorMessage( ex.Message );
            }
        }
        [HttpPost("/requestCallback/{pin}")]
        public async Task<ActionResult> requestCallback(string pin)
        {
            try
            {
                Console.WriteLine("requestCallback/{0}", pin);

                if (string.IsNullOrEmpty(pin))
                {
                    return ReturnErrorMessage("Missing argument 'pin' in body");
                }
                _cache.Set(pin, true.ToString() );
                return new OkResult();
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }
        [HttpGet("/checkCallback/{pin}")]
        public async Task<ActionResult> checkCallback(string pin)
        {
            try
            {
                Console.WriteLine("checkCallback/{0}", pin);
                if (string.IsNullOrEmpty(pin))
                {
                    return ReturnErrorMessage("Missing argument 'pin'");
                }
                string buf = "false";
                if (!_cache.TryGetValue(pin, out buf))
                    buf = "false";
                bool wasCallbackHit = false;
                bool.TryParse(buf, out wasCallbackHit);
                if ( wasCallbackHit )
                {
                    _cache.Remove(pin);
                    return new ContentResult { ContentType = "text/plain", Content = pin };
                }
                else
                {
                    return ReturnErrorMessage("callback url was not hit yet for specific request: " + pin);
                }
            }
            catch (Exception ex)
            {
                return ReturnErrorMessage(ex.Message);
            }
        }

    } // cls
} // ns
