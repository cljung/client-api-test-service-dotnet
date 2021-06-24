using System.Net;

namespace AA.DIDApi.Models
{
    public class HttpActionResponse
    {
        public HttpStatusCode StatusCode { get; set; }

        public bool IsSuccessStatusCode { get; set; }

        public string ResponseContent { get; set; }
    }
}
