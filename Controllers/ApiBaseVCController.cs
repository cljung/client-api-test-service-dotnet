using AA.DIDApi.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace AA.DIDApi.Controllers
{
    public abstract class ApiBaseVCController : ControllerBase
    {
        protected IMemoryCache _cache;
        protected readonly IWebHostEnvironment _env;
        //protected readonly ILogger<ApiBaseVCController> _log;
        protected readonly AppSettingsModel AppSettings;
        protected readonly IConfiguration _configuration;

        public ApiBaseVCController(
            IConfiguration configuration,
            IOptions<AppSettingsModel> appSettings,
            IMemoryCache memoryCache,
            IWebHostEnvironment env
            //,ILogger<ApiBaseVCController> log
            )
        {
            this.AppSettings = appSettings.Value;
            _cache = memoryCache;
            _env = env;
            //_log = log;
            _configuration = configuration;
        }

        protected string GetRequestHostName()
        {
            string scheme = "https"; // : this.Request.Scheme;
            string originalHost = Request.Headers["x-original-host"];

            return !string.IsNullOrEmpty(originalHost)
                ? $"{scheme}://{originalHost}"
                : $"{scheme}://{Request.Host}";
        }

        // return 400 error-message
        protected ActionResult ReturnErrorMessage(string errorMessage)
        {
            return BadRequest(new 
                {
                    error = "400", 
                    error_description = errorMessage 
                });
        }

        // return 200 json 
        protected ActionResult ReturnJson(string json)
        {
            return new ContentResult { ContentType = "application/json", Content = json };
        }

        protected ActionResult ReturnErrorB2C(string message)
        {
            var msg = new 
            {
                version = "1.0.0",
                status = 400,
                userMessage = message
            };
            return new ContentResult { StatusCode = 409, ContentType = "application/json", Content = JsonConvert.SerializeObject(msg) };
        }

        // POST to VC Client API
        protected async Task<HttpActionResponse> HttpPostAsync(string body)
        {
            try
            {
                //_log.LogInformation($"POST request initializing\n{body}");

                using HttpClient client = new HttpClient();
                using HttpResponseMessage res = await client.PostAsync(this.AppSettings.ApiEndpoint, new StringContent(body, Encoding.UTF8, "application/json"));
                string response = res.Content.ReadAsStringAsync().Result;

                return new HttpActionResponse
                {
                    StatusCode = res.StatusCode,
                    IsSuccessStatusCode = res.IsSuccessStatusCode,
                    ResponseContent = response
                };
            }
            catch (Exception ex)
            {
                return new HttpActionResponse
                {
                    StatusCode = HttpStatusCode.GatewayTimeout,
                    IsSuccessStatusCode = false,
                    ResponseContent = ex.Message
                };
            }
        }

        // GET
        protected async Task<HttpActionResponse> HttpGetAsync(string url)
        {
            using HttpClient client = new HttpClient();
            using HttpResponseMessage res = await client.GetAsync(url);
            string response = await res.Content.ReadAsStringAsync();

            return new HttpActionResponse
            {
                StatusCode = res.StatusCode,
                IsSuccessStatusCode = res.IsSuccessStatusCode,
                ResponseContent = response
            };
        }

        protected void TraceHttpRequest()
        {
            string xForwardedFor = Request.Headers["X-Forwarded-For"];
            string ipaddr = !string.IsNullOrEmpty(xForwardedFor)
                ? xForwardedFor
                : HttpContext.Connection.RemoteIpAddress.ToString(); 
            
            //_log.LogInformation($"{DateTime.UtcNow:o} {ipaddr} -> {Request.Method} {Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}");
        }

        protected async Task<string> GetRequestBodyAsync()
        {
            using StreamReader reader = new StreamReader(Request.Body);
            return await reader.ReadToEndAsync();
        }

        protected JObject JWTTokenToJObject(string token)
        {
            string[] parts = token.Split(".");
            parts[1] = parts[1].PadRight(4 * ((parts[1].Length + 3) / 4), '=');

            return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
        }

        protected bool GetCachedValue(string key, out string value)
        {
            return _cache.TryGetValue(key, out value);
        }

        protected bool GetCachedJsonObject(string key, out JObject value)
        {
            value = null;
            if (!_cache.TryGetValue(key, out string buf))
            {
                return false;
            } 
            else 
            {
                value = JObject.Parse(buf);
                return true;
            }
        }

        protected void CacheJsonObjectWithExpiration(string key, object jsonObject)
        {
            _cache.Set(key, JsonConvert.SerializeObject(jsonObject), DateTimeOffset.Now.AddSeconds(this.AppSettings.CacheExpiresInSeconds));
        }

        protected void CacheValueWithNoExpiration(string key, string value)
        {
            _cache.Set(key, value);
        }

        protected void RemoveCacheValue(string key)
        {
            _cache.Remove(key);
        }
    }
}
