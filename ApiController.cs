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
    [Route("[action]")]
    [ApiController]
    public class ApiController : ControllerBase
    {
        private IMemoryCache _cache;
        private readonly IHostingEnvironment _env;
        private readonly AppSettingsModel AppSettings;

        private const string IssuanceRequestConfigFile = "issuance_request_config.json";
        private const string PresentationRequestConfigFile = "presentation_request_config.json";

        public ApiController(IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, IHostingEnvironment env)
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _env = env;
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Helpers
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////


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
        private void SetCookie( string key, string body ) 
        {
            CookieOptions option = new CookieOptions();
            option.HttpOnly = true;
            option.Domain = this.Request.Host.Host;
            option.Path = "/";
            option.SameSite = SameSiteMode.None;
            option.Secure = this.Request.IsHttps;
            option.Expires = DateTime.UtcNow.AddSeconds(this.AppSettings.CookieExpiresInSeconds);
            string b64 = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(body));
            this.Response.Cookies.Append( key, b64, option);
        }

        // get a cookie
        private string GetCookie( string key )
        {
            string cookie = null;
            if (this.Request.Cookies.TryGetValue( key , out cookie )) {
                try {
                    cookie = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(cookie));
                } catch (Exception ex) {
                }
            }
            return cookie;
        }
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
        private string GetRequestBody()
        {
            string body = null;
            using (var reader = new System.IO.StreamReader(this.Request.Body)) {
                body = reader.ReadToEnd();
            }
            return body;
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// REST APIs
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

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
        public async Task<ActionResult> presentation()
        {
            try {
                return SendStaticJsonFile( PresentationRequestConfigFile );
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }

        [HttpGet]
        public async Task<ActionResult> issuanceReference()
        {
            try {
                int pinMaxValue = int.Parse("".PadRight(this.AppSettings.PinCodeLength, '9'));          // 9999999
                int randomNumber = RandomNumberGenerator.GetInt32( 1, pinMaxValue );
                string pin = string.Format("{0:D" + this.AppSettings.PinCodeLength.ToString() + "}", randomNumber);
                string jsonString = ReadFile( IssuanceRequestConfigFile );
                if ( string.IsNullOrEmpty(jsonString) ) {
                    return ReturnErrorMessage( IssuanceRequestConfigFile + " not found" );
                }
                JObject issuanceRequestConfig = JObject.Parse(jsonString);
                issuanceRequestConfig["issuance"]["pin"]["value"] = pin;
                string urlCallback = issuanceRequestConfig["issuance"]["callback"].ToString();
                if (string.IsNullOrWhiteSpace(urlCallback))
                    urlCallback = "https://did-test-client.azurewebsites.net/requestCallback/";
                if (!urlCallback.EndsWith("/"))
                    urlCallback += "/";
                issuanceRequestConfig["issuance"]["callback"] = $"{urlCallback}{pin}";
                jsonString = JsonConvert.SerializeObject(issuanceRequestConfig);
                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out contents) ) {
                    return ReturnErrorMessage( contents );
                }
                JObject requestConfig = JObject.Parse(contents);
                requestConfig["pin"] = pin;
                jsonString = JsonConvert.SerializeObject(requestConfig);
                return ReturnJson( jsonString );
            }  catch (Exception ex)  {
                return ReturnErrorMessage( ex.Message );
            }
        }

        [HttpGet]
        public async Task<ActionResult> presentationReference()
        {
            try {
                string state = Guid.NewGuid().ToString();
                string jsonString = ReadFile(PresentationRequestConfigFile);
                if (string.IsNullOrEmpty(jsonString)) {
                    return ReturnErrorMessage( PresentationRequestConfigFile + " not found" );
                }
                JObject config = JObject.Parse(jsonString);
                config["presentation"]["state"] = state;
                jsonString = JsonConvert.SerializeObject(config);
                string contents = "";
                HttpStatusCode statusCode = HttpStatusCode.OK;
                if ( !HttpPost(jsonString, out statusCode, out contents))  {
                    return ReturnErrorMessage( contents );
                }
                SetCookie(this.AppSettings.CookieKey, state); 
                return ReturnJson( contents );
            }  catch (Exception ex) {
                return ReturnErrorMessage(ex.Message);
            }
        }
        [HttpPost]
        public async Task<ActionResult> response()
        {
            try {
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

        [HttpGet]
        public async Task<ActionResult> checkResponse()
        {
            try {
                string state = GetCookie( this.AppSettings.CookieKey );
                if ( string.IsNullOrEmpty(state)) {
                    return ReturnErrorMessage( "No state saved in cookies" );
                }
                string body = null;
                if (_cache.TryGetValue( state, out body)) {
                    return ReturnJson(body);
                } else {
                    return ReturnErrorMessage( "No claims for state: " + state );
                }
            } catch (Exception ex) {
                return ReturnErrorMessage( ex.Message );
            }
        }
    } // cls
} // ns
