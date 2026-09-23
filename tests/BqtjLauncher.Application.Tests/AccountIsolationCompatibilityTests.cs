using System.IO;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

public sealed class AccountIsolationCompatibilityTests
{
    [Fact]
    public void MonikerOnlyDependsOnImmutableAccountId()
    {
        var accountId = Guid.Parse("12345678-1234-5678-90ab-1234567890ab");

        var moniker = AccountIsolationIdentity.CreateMoniker(accountId);

        Assert.Equal("Derawaze.Bqtj.Account.123456781234567890ab1234567890ab", moniker);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("ends \\", "\"ends \\\\\"")]
    public void CommandLineArgumentQuotingMatchesWindowsRules(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.QuoteArgument(argument));
    }

    [Fact]
    public void ManifestIsDeterministicAndExcludesLocalStateFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bqtj-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, IsolatedPayloadManifest.EntryPointFileName), "desktop");
            File.WriteAllText(Path.Combine(root, IsolatedPayloadManifest.NativeHostFileName), "native");
            File.WriteAllText(Path.Combine(root, "runtime.dll"), "runtime");
            File.WriteAllText(Path.Combine(root, "account.db"), "must-not-stage");
            File.WriteAllText(Path.Combine(root, "launcher.log"), "must-not-stage");

            var first = IsolatedPayloadManifest.Create(root);
            var second = IsolatedPayloadManifest.Create(root);

            Assert.Equal(first.ManifestHash, second.ManifestHash);
            Assert.Equal(3, first.Files.Count);
            Assert.DoesNotContain(
                first.Files,
                file => file.RelativePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                first.Files,
                file => file.RelativePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
