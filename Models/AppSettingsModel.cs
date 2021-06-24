namespace AA.DIDApi.Models
{
    public class AppSettingsModel
    {
        public string ApiEndpoint { get; set; }

        public string ApiKey { get; set; }

        public string UseAkaMs { get; set; }

        public string CookieKey { get; set; }

        public int CookieExpiresInSeconds { get; set; }

        public int CacheExpiresInSeconds { get; set; }

        public string ActiveCredentialType { get; set; }

        public string client_name { get; set; }
    }
}
