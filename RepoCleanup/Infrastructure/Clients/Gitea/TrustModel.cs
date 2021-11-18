namespace RepoCleanup.Infrastructure.Clients.Gitea
{
    public enum TrustModel
    {
        @default,
        collaborator,
        committer,
        collaboratorcommitter
    }
}
