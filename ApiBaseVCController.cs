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
    public class ApiBaseVCController : ControllerBase
    {
        protected IMemoryCache _cache;
        protected readonly IWebHostEnvironment _env;
        protected readonly ILogger<ApiBaseVCController> _log;
        protected readonly AppSettingsModel AppSettings;

        public ApiBaseVCController(IOptions<AppSettingsModel> appSettings, IMemoryCache memoryCache, IWebHostEnvironment env, ILogger<ApiBaseVCController> log)
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _env = env;
            _log = log;
        }

        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Helpers
        /// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        protected string GetRequestHostName()
        {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty(originalHost))
                 hostname = string.Format("{0}://{1}", scheme, originalHost);
            else hostname = string.Format("{0}://{1}", scheme, this.Request.Host);
            return hostname;
        }
        // return 400 error-message
        protected ActionResult ReturnErrorMessage(string errorMessage)
        {
            return BadRequest(new { error = "400", error_description = errorMessage });
        }
        // return 200 json 
        protected ActionResult ReturnJson( string json )
        {
            return new ContentResult { ContentType = "application/json", Content = json };
        }
        protected ActionResult ReturnErrorB2C(string message)
        {
            var msg = new
            {
                version = "1.0.0", status = 400, userMessage = message
            };
            return new ContentResult { StatusCode = 409, ContentType = "application/json", Content = JsonConvert.SerializeObject(msg) };
        }
        // read & cache the file
        protected string ReadFile(string filename)
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
        protected ActionResult SendStaticJsonFile(string filename)
        {
            string json = ReadFile(filename);
            if (!string.IsNullOrEmpty(json)) {
                return ReturnJson( json );
            } else {
                return ReturnErrorMessage( filename + " not found" );
            }
        }

        // set a cookie
        protected bool HttpPost(string body, out HttpStatusCode statusCode, out string response)
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
        protected bool HttpGet(string url, out HttpStatusCode statusCode, out string response)
        {
            response = null;
            HttpClient client = new HttpClient();
            HttpResponseMessage res = client.GetAsync( url ).Result;
            response = res.Content.ReadAsStringAsync().Result;
            client.Dispose();
            statusCode = res.StatusCode;
            return res.IsSuccessStatusCode;
        }

        protected void TraceHttpRequest()
        {
            string ipaddr = "";
            string xForwardedFor = this.Request.Headers["X-Forwarded-For"];
            if (!string.IsNullOrEmpty(xForwardedFor))
                 ipaddr = xForwardedFor;
            else ipaddr = HttpContext.Connection.RemoteIpAddress.ToString();
            _log.LogTrace("{0} {1} -> {2} {3}://{4}{5}{6}", DateTime.UtcNow.ToString("o"), ipaddr
                    , this.Request.Method, this.Request.Scheme, this.Request.Host, this.Request.Path, this.Request.QueryString );
        }
        protected string GetRequestBody()
        {
            return new System.IO.StreamReader(this.Request.Body).ReadToEndAsync().Result;
        }

        protected JObject JWTTokenToJObject( string token )
        {
            string[] parts = token.Split(".");
            parts[1] = parts[1].PadRight(4 * ((parts[1].Length + 3) / 4), '=');
            return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
        }

        protected string GetDidManifest()
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

        protected bool GetCachedValue(string key, out string value)
        {
            return _cache.TryGetValue(key, out value);
        }
        protected bool GetCachedJsonObject(string key, out JObject value)
        {
            value = null;
            if ( !_cache.TryGetValue(key, out string buf) ) {
                return false;
            } else {
                value = JObject.Parse(buf);
                return true;
            }
        }
        protected void CacheJsonObject( string key, object jsonObject )
        {
            _cache.Set( key, JsonConvert.SerializeObject(jsonObject), DateTimeOffset.Now.AddSeconds(this.AppSettings.CacheExpiresInSeconds));
        }
        protected void CacheValue(string key, string value)
        {
            _cache.Set(key, value, DateTimeOffset.Now.AddSeconds(this.AppSettings.CacheExpiresInSeconds));
        }
        protected void RemoveCacheValue( string key )
        {
            _cache.Remove(key);
        }
    } // cls
} // ns
