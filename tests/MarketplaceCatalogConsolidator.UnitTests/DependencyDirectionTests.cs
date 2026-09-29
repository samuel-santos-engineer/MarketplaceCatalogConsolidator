using System.Xml.Linq;

namespace MarketplaceCatalogConsolidator.UnitTests;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void ApplicationProjectReferencesDomainButNotInfrastructure()
    {
        var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot.FullName, "MarketplaceCatalogConsolidator.sln")))
        {
            repositoryRoot = repositoryRoot.Parent;
        }

        Assert.NotNull(repositoryRoot);
        var applicationProject = Path.Combine(
            repositoryRoot.FullName,
            "src",
            "MarketplaceCatalogConsolidator.Application",
            "MarketplaceCatalogConsolidator.Application.csproj");
        var references = XDocument.Load(applicationProject)
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(include => include is not null)
            .ToArray();

        Assert.Contains(references, include => include!.EndsWith("MarketplaceCatalogConsolidator.Domain.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, include => include!.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
    }
}
