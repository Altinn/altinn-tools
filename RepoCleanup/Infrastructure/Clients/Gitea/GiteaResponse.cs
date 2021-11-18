using System;
using System.Net;

namespace RepoCleanup.Infrastructure.Clients.Gitea
{
    public class GiteaResponse
    {
        public Exception Exception { get; set; }
        public HttpStatusCode StatusCode { get; set; }

        public string ResponseMessage { get; set; }

        public bool Success { get; set; }
    }
}
