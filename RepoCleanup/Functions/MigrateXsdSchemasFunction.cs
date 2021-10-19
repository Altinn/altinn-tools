using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using RepoCleanup.Application.Commands;
using RepoCleanup.Application.CommandHandlers;
using RepoCleanup.Services;
using RepoCleanup.Utils;

namespace RepoCleanup.Functions
{
    public static class MigrateXsdSchemasFunction
    {
        public static async Task Run()
        {
            NotALogger logger = new("MigrateXsdSchemas - Log.txt");
            logger.AddNothing();

            SharedFunctionSnippets.WriteHeader("Migrating XSD Schemas from active services in Altinn II");

            string basePath = CollectMigrationWorkFolder();
            List<string> organisations = await SharedFunctionSnippets.CollectOrgInfo();

            logger.AddInformation($"Started!");
            logger.AddInformation($"Using '{basePath}' as base path for all organisations");

            try
            {
                MigrateAltinn2FormSchemasCommandHandler migrateAltinn2FormSchemasCommandHandler = new(new GiteaService(), logger);
                MigrateAltinn2FormSchemasCommand migrateAltinn2FormSchemasCommand = new(organisations, basePath);
                await migrateAltinn2FormSchemasCommandHandler.Handle(migrateAltinn2FormSchemasCommand);
            }
            catch (Exception exception)
            {
                logger.AddError(exception);
                throw;
            }
            finally
            {
                logger.WriteLog();
            }
        }

        private static string CollectMigrationWorkFolder()
        {
            Console.WriteLine("This operation requires a folder to which it can clone the datamodels repositories.");
            string basePath = SharedFunctionSnippets.CollectInput("Provide folder path (folder should be empty): ");

            if (!Directory.Exists(basePath))
            {
                Console.WriteLine("Can't find the specified folder!");
                return CollectMigrationWorkFolder();
            }

            return basePath;
        }
    }
}
