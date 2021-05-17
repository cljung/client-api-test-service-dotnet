using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace client_api_test_service_dotnet.Models
{
    public class SelfAssertedClaim
    {
        public string claim { get; set; }
    }
    public class AppSettingsModelVC
    {
        public int PinCodeLength { get; set; }
        public string didIssuer { get; set; }
        public string didVerifier { get; set; }
        public string manifest { get; set; }
        public string credentialType { get; set; }
        public string client_logo_uri { get; set; }
        public string client_tos_uri { get; set; }
        public string client_purpose { get; set; }
        public List<SelfAssertedClaim> selfAssertedClaims { get; set; }
    }
}
