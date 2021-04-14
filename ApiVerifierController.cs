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
    [Route("api/verifier/[action]")]
    [ApiController]
    public class ApiVerifierController : ControllerBase
    {
        private IMemoryCache _cache;
        private readonly IHostingEnvironment _env;
        private readonly AppSettingsModel AppSettings;

        private const string PresentationRequestConfigFile = "presentation_request_config.json";

        public ApiVerifierController(IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, IHostingEnvironment env)
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
            return string.Format("{0}/api/verifier", GetRequestHostName());
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
            HttpResponseMessage res = client.GetAsync( url ).Result;
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

        private JObject JWTTokenToJObject( string token )
        {
            string[] parts = token.Split(".");
            parts[1] = parts[1].PadRight(4 * ((parts[1].Length + 3) / 4), '=');
            return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
        }

        private string GetDidManifest()
        {
            string contents = null;
            if (!_cache.TryGetValue("manifest", out contents)) {
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if (HttpGet(AppSettings.manifest, out statusCode, out contents)) {
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
            try {
                JObject manifest = JObject.Parse(GetDidManifest());
                var info = new
                {
                    date = DateTime.Now.ToString(),
                    host = GetRequestHostName(),
                    api = GetApiPath(),
                    didIssuer = AppSettings.didIssuer,
                    didVerifier = AppSettings.didVerifier,
                    credentialType = AppSettings.credentialType,
                    client_purpose = AppSettings.client_purpose,
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
        public async Task<ActionResult> logo()
        {
            return Redirect(AppSettings.client_logo_uri);
        }

        [HttpGet]
        public async Task<ActionResult> presentation()
        {
            try {
                return SendStaticJsonFile(PresentationRequestConfigFile);
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }

        [HttpGet]
        public async Task<ActionResult> presentationReference()
        {
            try {
                string jsonString = ReadFile(PresentationRequestConfigFile);
                if (string.IsNullOrEmpty(jsonString)) {
                    return ReturnErrorMessage( PresentationRequestConfigFile + " not found" );
                }
                string state = Guid.NewGuid().ToString();
                string nonce = Guid.NewGuid().ToString();
                JObject config = JObject.Parse(jsonString);
                config["authority"] = AppSettings.didVerifier;
                config["registration"]["clientName"] = AppSettings.client_name;
                config["registration"]["logoUrl"] = AppSettings.client_logo_uri;
                config["presentation"]["callback"] = string.Format("{0}/presentationCallback", GetApiPath());
                config["presentation"]["state"] = state;
                config["presentation"]["nonce"] = nonce;
                config["presentation"]["requestedCredentials"][0]["type"] = AppSettings.credentialType;
                config["presentation"]["requestedCredentials"][0]["manifest"] = AppSettings.manifest;
                config["presentation"]["requestedCredentials"][0]["purpose"] = AppSettings.client_purpose;
                config["presentation"]["requestedCredentials"][0]["trustedIssuers"][0] = AppSettings.didIssuer;
                jsonString = JsonConvert.SerializeObject(config);
                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out contents))  {
                    return ReturnErrorMessage( contents );
                }
                JObject apiResp = JObject.Parse(contents);
                //  iOS Authenticator doesn't allow redirects
                apiResp["url"] = apiResp["url"].ToString().Replace("https://aka.ms/vcrequest?", "https://draft.azure-api.net/api/client/v1.0/request?");
                apiResp.Add(new JProperty("id", state));
                contents = JsonConvert.SerializeObject(apiResp);
                return ReturnJson( contents );
            }  catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult> presentationCallback()
        {
            try {
                string body = GetRequestBody();
                JObject presentationResponse = JObject.Parse(body);
                if (presentationResponse["message"].ToString() == "request_retrieved") {
                    string requestId = presentationResponse["requestId"].ToString();
                    var cacheData = new {
                        status = 1,
                        message = "QR Code is scanned. Waiting for validation...",
                        vc = ""
                    };
                    _cache.Set(requestId, JsonConvert.SerializeObject(cacheData));
                }
                if (presentationResponse["message"].ToString() == "presentation_verified") {
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
                    _cache.Set(state, JsonConvert.SerializeObject(cacheData));
                }
                return new OkResult();
            } catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }

        [HttpGet]
        public async Task<ActionResult> presentationResponse()
        {
            try {
                string state = this.Request.Query["id"];
                if (string.IsNullOrEmpty(state)) {
                    return ReturnErrorMessage("Missing argument 'id'");
                }
                string body = null;
                if (_cache.TryGetValue( state, out body)) {
                    _cache.Remove( state );
                    return ReturnJson(body);
                } else {
                    //return ReturnErrorMessage( "No claims for state: " + state );
                    string requestId = this.Request.Query["requestId"];
                    if (!string.IsNullOrEmpty(requestId) && _cache.TryGetValue(requestId, out body)) {
                        _cache.Remove(requestId);
                        return ReturnJson(body);
                    }
                }
                return new OkResult();
                //return ReturnJson(JsonConvert.SerializeObject(info));
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }
    } // cls
} // ns
