namespace Altinn.Apps.Monitoring.Application;

internal static class QueryLibrary
{
    // Checks for requests against the apps Roles API
    public const string GetRolesQuery = """
            AppRequests
            | where TimeGenerated >= datetime('{0}') and TimeGenerated < datetime('{1}')
            | where Name == 'GET Authorization/GetRolesForCurrentParty [app/org]'
            | summarize ['Value'] = sum(ItemCount) by bin(TimeGenerated, 1d), App = AppRoleName, AppVersion, Name
            | order by TimeGenerated desc
        """;
}
