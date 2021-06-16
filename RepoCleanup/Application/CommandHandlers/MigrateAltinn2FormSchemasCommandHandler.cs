﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using LibGit2Sharp;

using RepoCleanup.Application.Commands;
using RepoCleanup.Models;
using RepoCleanup.Services;
using RepoCleanup.Utils;

namespace RepoCleanup.Application.CommandHandlers
{
    public class MigrateAltinn2FormSchemasCommandHandler
    {
        private readonly GiteaService _giteaService;
        private readonly NotALogger _logger;

        public MigrateAltinn2FormSchemasCommandHandler(GiteaService giteaService, NotALogger logger)
        {
            _giteaService = giteaService;
            _logger = logger;
        }

        public async Task Handle(MigrateAltinn2FormSchemasCommand command)
        {
            List<Altinn2Service> allReportingServices = await AltinnServiceRepository.GetReportingServices();

            foreach (string organisation in command.Organisations)
            {
                _logger.AddInformation($"Application owner: {organisation}");

                string orgFolder = $"{command.WorkPath}\\{organisation}";
                string repoName = $"{organisation}-datamodels";
                string repoFolder = $"{orgFolder}\\{repoName}";
                string remotePath = $"{Globals.RepositoryBaseUrl}/{organisation}/{repoName}";

                Directory.CreateDirectory(orgFolder);

                if (!Directory.Exists(repoFolder))
                {
                    CloneRepository(repoFolder, remotePath);
                }

                await CreateFolderStructure(repoFolder);

                StringBuilder reportBuilder = new();
                reportBuilder.AppendLine("# Migration report");
                reportBuilder.AppendLine($"\nMigration performed '{DateTime.Now}'");

                List<Altinn2Service> organisationReportingServices =
                    allReportingServices.Where(s => s.ServiceOwnerCode.ToLower() == organisation).ToList();

                if (organisationReportingServices.Count <= 0)
                {
                    reportBuilder.AppendLine("\nNo services found in Altinn 2 production.");
                }

                foreach (Altinn2Service service in organisationReportingServices)
                {
                    await DownloadFormSchemasForService(service, repoFolder);
                    reportBuilder.AppendLine($"\nDownloaded XSD schemas for forms in service: {service.ServiceName}");
                }

                await System.IO.File.WriteAllTextAsync($"{repoFolder}\\altinn2\\readme.md", reportBuilder.ToString());

                List<string> changedFiles = Status(repoFolder);

                if (changedFiles.Count > 0)
                {
                    await CommitChanges(repoFolder);
                    PushChanges(repoFolder);
                }
            }
        }

        private async Task DownloadFormSchemasForService(Altinn2Service service, string repositoryFolder)
        {
            _logger.AddInformation($"Service: {service.ServiceName}");

            Altinn2ReportingService reportingService = await AltinnServiceRepository.GetReportingService(service);

            string serviceName = $"{service.ServiceCode}-{service.ServiceEditionCode}"
                + $"-{service.ServiceName.AsFileName(false)}";
            serviceName = serviceName.Substring(0, Math.Min(serviceName.Length, 80)).TrimEnd(' ', '.', ',');
            string serviceDirectory = $"{repositoryFolder}\\altinn2\\{serviceName}";

            Directory.CreateDirectory(serviceDirectory);

            foreach (Altinn2Form formMetaData in reportingService.FormsMetaData)
            {
                XDocument xsdDocument = await AltinnServiceRepository.GetFormXsd(service, formMetaData);
                if (xsdDocument == null)
                {
                    _logger.AddInformation($"DataFormatId: {formMetaData.DataFormatID}-{formMetaData.DataFormatVersion} NOT FOUND");
                    continue;
                }

                string fileName = $"{serviceDirectory}\\{formMetaData.DataFormatID}-{formMetaData.DataFormatVersion}.xsd";
                using (FileStream fileStream = new FileStream(fileName, FileMode.OpenOrCreate))
                {
                    await xsdDocument.SaveAsync(fileStream, SaveOptions.None, CancellationToken.None);
                }
                
                _logger.AddInformation($"DataFormatId: {formMetaData.DataFormatID}-{formMetaData.DataFormatVersion} Downloaded.");
            }
        }

        private static async Task CreateFolderStructure(string repoFolder)
        {
            Directory.CreateDirectory($"{repoFolder}\\.altinnstudio");
            Directory.CreateDirectory($"{repoFolder}\\altinn2");
            Directory.CreateDirectory($"{repoFolder}\\shared");

            await System.IO.File.WriteAllTextAsync(
                $"{repoFolder}\\.altinnstudio\\settings.json",
                "{\n  \"repotype\" : \"datamodels\"\n}");

            await System.IO.File.WriteAllTextAsync(
                $"{repoFolder}\\shared\\README.md",
                "# Shared models");
        }

        private static void CloneRepository(string repoFolder, string remotePath)
        {
            CloneOptions cloneOptions = new()
            {
                CredentialsProvider = (a, b, c) => new UsernamePasswordCredentials
                {
                    Username = Globals.GiteaToken,
                    Password = string.Empty
                }
            };

            LibGit2Sharp.Repository.Clone(remotePath + ".git", repoFolder, cloneOptions);
        }

        private static List<string> Status(string localRepoPath)
        {
            List<string> repoContent = new List<string>();
            using (var repo = new LibGit2Sharp.Repository(localRepoPath))
            {
                LibGit2Sharp.Commands.Stage(repo, "*");
                RepositoryStatus status = repo.RetrieveStatus(new StatusOptions());
                foreach (StatusEntry item in status)
                {
                    repoContent.Add(item.FilePath);
                }
            }

            return repoContent;
        }

        private async Task CommitChanges(string localRepoPath)
        {
            using (var repo = new LibGit2Sharp.Repository(localRepoPath))
            {
                User user = await _giteaService.GetAuthenticatedUser();
                Signature author = new Signature(user.Username, "@jugglingnutcase", DateTime.Now);

                Commit commit = repo.Commit("Added XSD schemas copied from Altinn II", author, author);
            }
        }
        
        private static void PushChanges(string localRepoPath)
        {
            using (LibGit2Sharp.Repository repo = new(localRepoPath))
            {
                Remote remote = repo.Network.Remotes["origin"];

                PushOptions options = new()
                {
                    CredentialsProvider = (_url, _user, _cred) => new UsernamePasswordCredentials
                    {
                        Username = Globals.GiteaToken,
                        Password = string.Empty
                    }
                };

                repo.Network.Push(remote, @"refs/heads/master", options);
            }
        }
    }
}
