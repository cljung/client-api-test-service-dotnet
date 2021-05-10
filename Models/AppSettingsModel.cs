using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace client_api_test_service_dotnet.Models
{
    public class AppSettingsModel
    {
        public string ApiEndpoint { get; set; }
        public string ApiKey { get; set; }
        public string UseAkaMs { get; set; }

        public string CookieKey { get; set; }
        public int CookieExpiresInSeconds { get; set; }
        public int PinCodeLength { get; set; }

        public int CacheExpiresInSeconds { get; set; }

        public string didIssuer { get; set; }
        public string didVerifier { get; set; }
        public string manifest { get; set; }
        public string credentialType { get; set; }
        public string client_name { get; set; }
        public string client_logo_uri { get; set; }
        public string client_tos_uri { get; set; }
        public string client_purpose { get; set; }
    }
}
