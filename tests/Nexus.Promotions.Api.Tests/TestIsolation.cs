using System.Runtime.CompilerServices;

namespace Nexus.Promotions.Api.Tests;

/// <summary>
/// The API reads companies.json from its content root (the API project folder under test), a file the admin app writes
/// and git ignores. Without this, a developer who has used Settings > Companies gets their own companies in every test.
/// </summary>
static class TestIsolation
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("CompaniesFile", Path.Combine(Path.GetTempPath(), $"ape-no-companies-{Guid.NewGuid():N}.json"));
        // The existing tests are about promotions, not sign-in (AdminAuthTests covers that), and must not write users or keys into the project folder.
        Environment.SetEnvironmentVariable("Admin__RequireLogin", "false");
        Environment.SetEnvironmentVariable("Admin__DataDir", Path.Combine(Path.GetTempPath(), $"ape-test-data-{Guid.NewGuid():N}"));
    }
}
