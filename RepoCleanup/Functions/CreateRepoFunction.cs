﻿using RepoCleanup.Application.CommandHandlers;
using RepoCleanup.Application.Commands;
using RepoCleanup.Models;
using RepoCleanup.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RepoCleanup.Functions
{
    public static class CreateRepoFunction
    {
        public async static Task Run()
        {
            SharedFunctionSnippets.WriteHeader("Create new repository for organisation(s)");

            var orgs = await SharedFunctionSnippets.CollectOrgInfo();
            var prefixRepoNameWithOrg = SharedFunctionSnippets.ShouldRepoNameBePrefixedWithOrg();
            var repoName = SharedFunctionSnippets.CollectRepoName();

            SharedFunctionSnippets.ConfirmWithExit($"You are about to create a new repository for {orgs.Count} organisation(s). Proceed?", "Aborting, no repositories created.");            

            var command = new CreateRepoForOrgsCommand(orgs, repoName, prefixRepoNameWithOrg);
            var commandHander = new CreateRepoForOrgsCommandHandler(new GiteaService());
            var result = await commandHander.Handle(command);

            Console.WriteLine($"Created {result} repositories.");
        }        
    }
}
