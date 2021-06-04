using RepoCleanup.Models;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RepoCleanup.Services
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

        public static async Task<GiteaResponse> CreateTeam(string org, CreateTeamOption teamOption)
        {
            GiteaResponse result = new GiteaResponse();

            HttpContent content = new StringContent(JsonSerializer.Serialize(teamOption, Globals.SerializerOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage res = await Globals.Client.PostAsync($"orgs/{org}/teams", content);

            if (res.StatusCode == System.Net.HttpStatusCode.Created)
            {
                result.Success = true;
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
            }
            else
            {
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
                result.Success = false;
                result.StatusCode = res.StatusCode;
            }

            return result;
        }

        public static async Task<GiteaResponse> CreateOrg(Organisation organisation)
        {
            GiteaResponse result = new GiteaResponse();

            HttpContent content = new StringContent(JsonSerializer.Serialize(organisation, Globals.SerializerOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage res = await Globals.Client.PostAsync($"orgs", content);

            if (res.StatusCode == System.Net.HttpStatusCode.Created)
            {
                result.Success = true;
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
            }
            else
            {
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
                result.Success = false;
                result.StatusCode = res.StatusCode;
            }

            return result;
        }

        public async Task<GiteaResponse> CreateRepo(CreateRepoOption createRepoOption) 
        {
            GiteaResponse result = new GiteaResponse();

            HttpContent content = new StringContent(JsonSerializer.Serialize(createRepoOption, Globals.SerializerOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage res = await Globals.Client.PostAsync($"user/repos/", content);

            if (res.StatusCode == System.Net.HttpStatusCode.Created)
            {
                result.Success = true;
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
            }
            else
            {
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
                result.Success = false;
                result.StatusCode = res.StatusCode;
            }

            return result;
        }

        public async Task<GiteaResponse> TransferRepoOwnership(string owner, string repoName, string newOwner)
        {
            GiteaResponse result = new GiteaResponse();

            HttpContent content = new StringContent(JsonSerializer.Serialize(new TransferRepoOption(newOwner), Globals.SerializerOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage res = await Globals.Client.PostAsync($"repos/{owner}/{repoName}/transfer", content);

            if (res.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                result.Success = true;
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
            }
            else
            {
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
                result.Success = false;
                result.StatusCode = res.StatusCode;
            }

            return result;
        }

        public async Task<GiteaResponse> GetRepo(string org, string repoName)
        {
            HttpResponseMessage res = await Globals.Client.GetAsync($"repos/{org}/{repoName}");

            GiteaResponse result = new GiteaResponse();
            if (res.StatusCode == System.Net.HttpStatusCode.OK)
            {
                result.Success = true;
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
            }
            else
            {
                result.ResponseMessage = await res.Content.ReadAsStringAsync();
                result.Success = false;
                result.StatusCode = res.StatusCode;
            }

            return result;
        }

        public async Task<GiteaResponse> AddTeamToRepo(string org, string repoName, string teamName)
        {
            var teamResponse = await GetTeamFromRepo(org, repoName, teamName);

            GiteaResponse giteaResponse = new GiteaResponse();
            if(teamResponse.StatusCode ==  System.Net.HttpStatusCode.NotFound)
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

            GiteaResponse giteaResponse = new GiteaResponse();
            if (httpResponse.StatusCode == System.Net.HttpStatusCode.OK)
            {
                giteaResponse.Success = true;
                giteaResponse.ResponseMessage = await httpResponse.Content.ReadAsStringAsync();
            }
            else
            {
                giteaResponse.ResponseMessage = await httpResponse.Content.ReadAsStringAsync();
                giteaResponse.Success = false;
                giteaResponse.StatusCode = httpResponse.StatusCode;
            }

            return giteaResponse;
        }
    }
}
