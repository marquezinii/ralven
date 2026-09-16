using Xunit;

namespace Ralven.Tests.App;

public sealed class ReleaseAutomationContractTests
{
    [Fact]
    public void Promotion_PinsCandidateAndWaitsForRequiredChecksBeforeMergeAndTag()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "promote-release.yml"));

        var pin = workflow.IndexOf("candidate_sha=$candidate", StringComparison.Ordinal);
        var checks = workflow.IndexOf("gh pr checks $env:RELEASE_PR", pin, StringComparison.Ordinal);
        var gate = workflow.IndexOf("Required CI gate", checks, StringComparison.Ordinal);
        var merge = workflow.IndexOf("gh pr merge", gate, StringComparison.Ordinal);
        var target = workflow.IndexOf("Test-ReleaseTagTarget.ps1", merge, StringComparison.Ordinal);
        var tag = workflow.IndexOf("git tag --annotate", target, StringComparison.Ordinal);
        var push = workflow.IndexOf("git push origin \"refs/tags/$env:RELEASE_TAG\"", tag, StringComparison.Ordinal);

        Assert.True(pin >= 0);
        Assert.True(checks > pin);
        Assert.True(gate > checks);
        Assert.True(merge > gate);
        Assert.True(target > merge);
        Assert.True(tag > target);
        Assert.True(push > tag);
        Assert.Contains("--match-head-commit $env:CANDIDATE_SHA", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/release.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("Merge published main into dev/proxima-versao", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void StableRelease_HasSideEffectFreePlanAndRepositoryWidePublicationLock()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        Assert.Contains("group: ralven-stable-release", workflow, StringComparison.Ordinal);
        Assert.Contains("inputs.mode != 'plan'", workflow, StringComparison.Ordinal);
        Assert.Contains("inputs.mode == 'publish'", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.publish", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow_call:", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedBuild_CreatesArchivesAndInstallerOnlyAfterBrokerSignature()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var portable = workflow.IndexOf("Build-Portable.ps1 -Runtime win-x64 -Configuration Release -Harden -SkipArchives", StringComparison.Ordinal);
        var signature = workflow.IndexOf("Sign broker integrity manifest", portable, StringComparison.Ordinal);
        var finalizer = workflow.IndexOf("Finalize-BrokerIntegrity.ps1", signature, StringComparison.Ordinal);

        Assert.True(portable >= 0);
        Assert.True(signature > portable);
        Assert.True(finalizer > signature);
        Assert.DoesNotContain("Build-Installer.ps1", workflow[portable..signature], StringComparison.Ordinal);
    }

    [Fact]
    public void MainPush_DoesNotRepeatFullCandidateCi()
    {
        var root = TestHelpers.FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var push = workflow.IndexOf("push:", StringComparison.Ordinal);
        var pullRequest = workflow.IndexOf("pull_request:", push, StringComparison.Ordinal);

        Assert.True(push >= 0 && pullRequest > push);
        Assert.DoesNotContain("branches: [main", workflow[push..pullRequest], StringComparison.Ordinal);
        Assert.Contains("branches: [dev/proxima-versao]", workflow[push..pullRequest], StringComparison.Ordinal);
    }
}
