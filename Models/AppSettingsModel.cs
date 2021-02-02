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
        public string CookieKey { get; set; }
        public int CookieExpiresInSeconds { get; set; }
        public int PinCodeLength { get; set; }
    }
}
