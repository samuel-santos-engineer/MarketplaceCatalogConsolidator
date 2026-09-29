using YamlDotNet.RepresentationModel;

namespace MarketplaceCatalogConsolidator.UnitTests;

public sealed class RepositoryGovernanceTests
{
    [Fact]
    public void WorkflowParsesAndProvidesVerificationAndCredentialFreeSecretScanning()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MarketplaceCatalogConsolidator.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var path in Directory.GetFiles(Path.Combine(root.FullName, ".github", "workflows"), "*.yml"))
        {
            using var reader = File.OpenText(path);
            var yaml = new YamlStream();
            yaml.Load(reader);
            var document = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
            Assert.True(document.Children.ContainsKey(new YamlScalarNode("on")));
            var permissions = Assert.IsType<YamlMappingNode>(document.Children[new YamlScalarNode("permissions")]);
            Assert.Equal("read", Assert.IsType<YamlScalarNode>(permissions.Children[new YamlScalarNode("contents")]).Value);
            var jobs = Assert.IsType<YamlMappingNode>(document.Children[new YamlScalarNode("jobs")]);
            Assert.True(jobs.Children.ContainsKey(new YamlScalarNode("verify")));
            Assert.True(jobs.Children.ContainsKey(new YamlScalarNode("secrets")));
            var text = File.ReadAllText(path);
            Assert.Contains("sh scripts/verify.sh", text);
            Assert.Contains("gitleaks dir . --config .gitleaks.toml --redact --no-banner", text);
            Assert.Contains("gitleaks git . --config .gitleaks.toml --redact --no-banner", text);
            Assert.Contains("sha256sum --check --strict", text);
            Assert.DoesNotContain("secrets.", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pull_request_target", text);
            Assert.DoesNotContain("docker", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("python", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
