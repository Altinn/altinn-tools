using RepoCleanup.Models;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RepoCleanup.Infrastructure.Clients.Gitea
{
    public class GiteaService
    {
        public GiteaService()
        { }

        public static async Task<List<Organisation>> GetOrganisations()
        {
            HttpResponseMessage res = await Globals.Client.GetAsync("admin/orgs");
            string jsonString = await res.Content.ReadAsStringAsync();

            return JsonSerializer.Deserialize<List<Organisation>>(jsonString);
        }

        public static async Task<List<User>> GetUsers()
        {
            HttpResponseMessage res = await Globals.Client.GetAsync("admin/users");
            string jsonString = await res.Content.ReadAsStringAsync();

            return JsonSerializer.Deserialize<List<User>>(jsonString);
        }

        public async Task<User> GetAuthenticatedUser()
        {
            var response = await Globals.Client.GetAsync("user");
            string json = await response.Content.ReadAsStringAsync();

            return JsonSerializer.Deserialize<User>(json);
        }

        public static async Task<List<File>> GetRepoContent(Repository repo)
        {
            HttpResponseMessage res = await Globals.Client.GetAsync($"repos/{repo.Owner.Username}/{repo.Name}/contents");
            string jsonString = await res.Content.ReadAsStringAsync();

            return JsonSerializer.Deserialize<List<File>>(jsonString);
        }

        public async Task<List<Team>> GetTeam(string org)
        {
            var response = await Globals.Client.GetAsync($"orgs/{org}/teams");
            string json = await response.Content.ReadAsStringAsync();

            return JsonSerializer.Deserialize<List<Team>>(json);
        }

        public async Task<GiteaResponse> CreateTeam(string org, CreateTeamOption teamOption)
        {
            HttpContent content = new StringContent(JsonSerializer.Serialize(teamOption, Globals.SerializerOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage httpResponse = await Globals.Client.PostAsync($"orgs/{org}/teams", content);

            GiteaResponse giteResponse = await CreateGiteaResponse(httpResponse, HttpStatusCode.Created);

            return giteResponse;
        }

        public async Task<GiteaResponse> CreateOrg(Organisation organisation)
        {
            HttpContent content = new StringContent(JsonSerializer.Serialize(organisation, Globals.SerializerOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage httpResponse = await Globals.Client.PostAsync($"orgs", content);

            GiteaResponse giteaResponse = await CreateGiteaResponse(httpResponse, HttpStatusCode.Created);

            return giteaResponse;
        }

        public async Task<GiteaResponse> CreateRepo(CreateRepoOption createRepoOption)
        {
            HttpContent content = new StringContent(JsonSerializer.Serialize(createRepoOption, Globals.SerializerOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage httpResponse = await Globals.Client.PostAsync($"user/repos/", content);

            GiteaResponse result = await CreateGiteaResponse(httpResponse, HttpStatusCode.Created);

            return result;
        }

        public async Task<GiteaResponse> TransferRepoOwnership(string owner, string repoName, string newOwner)
        {
            HttpContent content = new StringContent(JsonSerializer.Serialize(new TransferRepoOption(newOwner), Globals.SerializerOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage httpResponse = await Globals.Client.PostAsync($"repos/{owner}/{repoName}/transfer", content);

            GiteaResponse giteaResponse = await CreateGiteaResponse(httpResponse, HttpStatusCode.Accepted);

            return giteaResponse;
        }

        public async Task<GiteaResponse> GetRepo(string org, string repoName)
        {
            HttpResponseMessage httpResponse = await Globals.Client.GetAsync($"repos/{org}/{repoName}");

            GiteaResponse giteaResponse = await CreateGiteaResponse(httpResponse, HttpStatusCode.OK);

            return giteaResponse;
        }

        public async Task<GiteaResponse> DeleteRepository(string org, string repoName)
        {
            HttpResponseMessage httpResponse = await Globals.Client.DeleteAsync($"repos/{org}/{repoName}");

            GiteaResponse giteaResponse = await CreateGiteaResponse(httpResponse, HttpStatusCode.NoContent);

            return giteaResponse;
        }

        public async Task<GiteaResponse> AddTeamToRepo(string org, string repoName, string teamName)
        {
            var teamResponse = await GetTeamFromRepo(org, repoName, teamName);

            GiteaResponse giteaResponse = new GiteaResponse();
            if (teamResponse.StatusCode == HttpStatusCode.NotFound)
            {
                HttpContent httpContent = new StringContent("", Encoding.UTF8, "application/json");
                HttpResponseMessage httpResponse = await Globals.Client.PutAsync($"repos/{org}/{repoName}/teams/{teamName}", httpContent);

                giteaResponse.Success = true;
                giteaResponse.StatusCode = httpResponse.StatusCode;
            }
            else
            {
                giteaResponse.Success = false;
            }

            return giteaResponse;
        }

        public async Task<GiteaResponse> GetTeamFromRepo(string org, string repoName, string teamName)
        {
            HttpResponseMessage httpResponse = await Globals.Client.GetAsync($"repos/{org}/{repoName}/teams/{teamName}");

            GiteaResponse giteaResponse = await CreateGiteaResponse(httpResponse, HttpStatusCode.OK);

            return giteaResponse;
        }

        private static async Task<GiteaResponse> CreateGiteaResponse(HttpResponseMessage httpResponse, HttpStatusCode httpStatusForSuccess)
        {
            GiteaResponse giteaResponse = new GiteaResponse();
            giteaResponse.Success = false;

            if (httpResponse.StatusCode == httpStatusForSuccess)
            {
                giteaResponse.Success = true;
            }

            giteaResponse.StatusCode = httpResponse.StatusCode;
            giteaResponse.ResponseMessage = await httpResponse.Content.ReadAsStringAsync();

            return giteaResponse;
        }
    }
}
