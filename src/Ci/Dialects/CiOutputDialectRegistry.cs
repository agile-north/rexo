namespace Rexo.Ci;

public static class CiOutputDialectRegistry
{
    public static ICiOutputDialect Resolve(string? provider)
    {
        return provider?.ToLowerInvariant() switch
        {
            "github-actions" => new GitHubActionsCiOutputDialect(),
            "azure-devops" => new AzureDevOpsCiOutputDialect(),
            "teamcity" => new TeamCityCiOutputDialect(),
            "gitlab-ci" => new GitLabCiOutputDialect(),
            "bitbucket-pipelines" => new BitbucketPipelinesCiOutputDialect(),
            _ => new GenericCiOutputDialect(provider),
        };
    }
}
