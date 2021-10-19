using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;

using Altinn.Studio.DataModeling.Converter.Xml;

using Json.Schema;

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

                await AddRepoSettings(repoFolder);

                Dictionary<string, string> schemaList = new Dictionary<string, string>();

                List<Altinn2Service> organisationReportingServices =
                    allReportingServices.Where(s => s.ServiceOwnerCode.ToLower() == organisation).ToList();

                foreach (Altinn2Service service in organisationReportingServices)
                {
                    Dictionary<string, string> serviceSchemas = await DownloadAndConvertFormSchemasForService(service, repoFolder);

                    schemaList = schemaList.Concat(serviceSchemas.Where(kvp => !schemaList.ContainsKey(kvp.Key)))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }

                await AddStudioFolder(repoFolder);

                await WriteReport(repoFolder, schemaList);

                List<string> changedFiles = Status(repoFolder);

                if (changedFiles.Count > 0)
                {
                    await CommitChanges(repoFolder);
                    PushChanges(repoFolder);
                }
            }
        }

        private static async Task WriteReport(string repoFolder, Dictionary<string, string> schemaList)
        {
            StringBuilder reportBuilder = new();
            reportBuilder.AppendLine("# List of schemas imported from Altinn II");
            reportBuilder.AppendLine();
            reportBuilder.AppendLine($"Import was performed: '{DateTime.Now}'");
            reportBuilder.AppendLine();
            reportBuilder.AppendLine($"(The list is not automatically maintained.)");
            reportBuilder.AppendLine();

            foreach (var schema in schemaList.OrderBy(kvp => kvp.Value))
            {
                reportBuilder.Append($"- {schema.Value} ");
                reportBuilder.Append($"[{schema.Key.Split('\\').Last()}]");
                reportBuilder.Append($"(.{schema.Key.Replace('\\', '/')})");
                reportBuilder.Append('\n');
            }

            if (schemaList.Count == 0)
            {
                reportBuilder.AppendLine($"Found no forms in Altinn II.");
            }

            await System.IO.File.WriteAllTextAsync($"{repoFolder}\\README.md", reportBuilder.ToString());
        }

        private async Task<Dictionary<string, string>> DownloadAndConvertFormSchemasForService(Altinn2Service service, string repositoryFolder)
        {
            _logger.AddInformation($"Service: {service.ServiceName}");

            Altinn2ReportingService reportingService = await AltinnServiceRepository.GetReportingService(service);

            Dictionary<string, string> schemas = new Dictionary<string, string>();

            foreach (Altinn2Form formMetaData in reportingService.FormsMetaData)
            {
                XDocument xsdDocument = await AltinnServiceRepository.GetFormXsd(service, formMetaData);
                if (xsdDocument == null)
                {
                    _logger.AddInformation($"Schema: {formMetaData.DataFormatID}-{formMetaData.DataFormatVersion} NOT FOUND");
                    continue;
                }

                string providerName = FindProvider(xsdDocument, formMetaData.DataFormatProviderType);
                string schemaVersionFolder = $"{repositoryFolder}\\{providerName}\\"
                    + $"{formMetaData.DataFormatID}\\{formMetaData.DataFormatVersion}";

                Directory.CreateDirectory(schemaVersionFolder);

                string fileName = CreatSchemaFileName(
                    formMetaData.DataFormatProviderType, formMetaData.DataFormatID, formMetaData.DataFormatVersion);
                string filePath = $"{schemaVersionFolder}\\{fileName}";

                // The schema might have been downloaded under a separate service.
                if (!System.IO.File.Exists(filePath))
                {
                    using (FileStream fileStream = new(filePath, FileMode.OpenOrCreate))
                    {
                        await xsdDocument.SaveAsync(fileStream, SaveOptions.None, CancellationToken.None);
                    }
                }

                await ConvertXmlToJsonSchema(filePath, fileName);
                _logger.AddInformation($"Schema: {formMetaData.DataFormatID}-{formMetaData.DataFormatVersion} Downloaded.");

                if (!schemas.ContainsKey(filePath.Substring(repositoryFolder.Length)))
                {
                    schemas.Add(filePath.Substring(repositoryFolder.Length), formMetaData.FormName ?? "no name");
                }
            }

            return schemas;
        }

        private async Task ConvertXmlToJsonSchema(string filePath, string fileName)
        {
            try
            {
                XmlSchemaToJsonSchemaConverter converter = new();
                JsonSchema json = converter.Convert(LoadXmlSchemaFromPath(filePath));

                await System.IO.File.WriteAllTextAsync(
                    filePath.Replace("xsd", "schema.json"),
                    System.Text.Json.JsonSerializer.Serialize(json, json.GetType(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            }
            catch (Exception e)
            {
                _logger.AddInformation($"Converting {fileName} to jsonSchema failed with exception {e}.");
            }
        }


        private static XmlSchema LoadXmlSchemaFromPath(string filePath)
        {
            using XmlReader xmlReader = XmlReader.Create(System.IO.File.OpenRead(filePath));
            var xmlSchema = XmlSchema.Read(xmlReader, (_, _) => { });

            var schemaSet = new XmlSchemaSet();
            schemaSet.Add(xmlSchema);
            schemaSet.Compile();

            return xmlSchema;
        }

        private string FindProvider(XDocument xsdDocument, string dataFormatProviderType)
        {
            XmlNamespaceManager namespaceManager = new XmlNamespaceManager(new NameTable());
            namespaceManager.AddNamespace("xsd", "http://www.w3.org/2001/XMLSchema");

            XElement dataFormatProviderElement = xsdDocument.XPathSelectElement(
                "xsd:schema/xsd:complexType/xsd:attribute[@name='dataFormatProvider']",
                namespaceManager);

            string foundProvider = dataFormatProviderType;
            if (dataFormatProviderElement != null)
            {
                foundProvider = dataFormatProviderElement.Attribute("fixed").Value;
            }

            switch (foundProvider.ToUpper())
            {
                case "OR":
                    return "oppgaveregisteret";
                default:
                    return foundProvider.ToLower();
            }
        }

        private static string CreatSchemaFileName(string dataFormatProviderType, string dataFormatID, int dataFormatVersion)
        {
            switch (dataFormatProviderType.ToUpper())
            {
                case "OR":
                    return $"melding-{dataFormatID}-{dataFormatVersion}.xsd";
                default:
                    return $"{dataFormatID}-{dataFormatVersion}.xsd";
            }
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

        private static async Task AddRepoSettings(string repoFolder)
        {
            Directory.CreateDirectory($"{repoFolder}\\.altinnstudio");

            await System.IO.File.WriteAllTextAsync(
                $"{repoFolder}\\.altinnstudio\\settings.json",
                "{\n  \"repoType\" : \"datamodels\"\n}");
        }

        private static async Task AddStudioFolder(string repoFolder)
        {
            Directory.CreateDirectory($"{repoFolder}\\studio");

            await System.IO.File.WriteAllTextAsync(
                $"{repoFolder}\\studio\\README.md",
                "# Studio data models\n\nSchemas created with the data model editor in altinn.studio");
        }
    }
}
