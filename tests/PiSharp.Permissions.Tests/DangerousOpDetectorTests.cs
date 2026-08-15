using System.Text.Json;
using PiSharp.Permissions;
using PiSharp.Permissions.Tests.Fakes;
using Xunit;

namespace PiSharp.Permissions.Tests;

public sealed class DangerousOpDetectorTests
{
    // --- Bash classification ---

    [Theory]
    [InlineData("git push origin main")]
    [InlineData("git push --force origin main")]
    [InlineData("GIT PUSH origin")]
    public void BashCategoryOf_GitPushCommands_ClassifyGitPush(string command)
    {
        Assert.Equal(DangerousOpDetector.GitPush, DangerousOpDetector.BashCategoryOf(command));
        Assert.NotEqual(DangerousOpDetector.Bash, DangerousOpDetector.BashCategoryOf(command));
    }

    [Theory]
    [InlineData("git reset --hard HEAD")]
    [InlineData("git reset --hard HEAD~2")]
    [InlineData("git -C /repo reset --hard")]
    public void BashCategoryOf_GitResetHard_ClassifyGitPush(string command)
    {
        Assert.Equal(DangerousOpDetector.GitPush, DangerousOpDetector.BashCategoryOf(command));
        Assert.NotEqual(DangerousOpDetector.Bash, DangerousOpDetector.BashCategoryOf(command));
    }

    [Theory]
    [InlineData("rm -rf /tmp/build")]
    [InlineData("rm --recursive --force /")]
    [InlineData("rm -fr /")]
    [InlineData("rm -r -f /")]
    [InlineData("rm -rfv /")]
    [InlineData("rm -f -r /")]
    [InlineData("sudo rm -rf /var/tmp")]
    [InlineData("cd /opt && rm -rf node_modules")]
    public void BashCategoryOf_RmRfVariants_ClassifyRmRf(string command)
    {
        Assert.Equal(DangerousOpDetector.RmRf, DangerousOpDetector.BashCategoryOf(command));
        Assert.NotEqual(DangerousOpDetector.Bash, DangerousOpDetector.BashCategoryOf(command));
        Assert.NotEqual(DangerousOpDetector.GitPush, DangerousOpDetector.BashCategoryOf(command));
    }


    [Fact]
    public void BashCategoryOf_LongFlagRecursiveForce_IsNotPlainBash()
    {
        // Regression: today RmRfPattern(\brm\s+-[a-zA-Z]*r[a-zA-Z]*f\b) misses
        // "--recursive --force" spelled with long flags.
        Assert.NotEqual(DangerousOpDetector.Bash, DangerousOpDetector.BashCategoryOf("rm --recursive --force /"));
        Assert.Equal(DangerousOpDetector.RmRf, DangerousOpDetector.BashCategoryOf("rm --recursive --force /"));
    }

    [Theory]
    [InlineData("rm -r /tmp")]
    [InlineData("rm -f /tmp/file")]
    [InlineData("rmdir -rf /tmp")]
    [InlineData("rm --recursive /tmp")]
    [InlineData("rm --force /tmp/file")]
    public void BashCategoryOf_NonRmRfCombos_ClassifyBash(string command)
    {
        Assert.Equal(DangerousOpDetector.Bash, DangerousOpDetector.BashCategoryOf(command));
    }

    [Fact]
    public void Category_PlainBash_IsBash()
    {
        var category = DangerousOpDetector.Category("bash", "{\"command\":\"ls\"}", null, "C:/project");
        Assert.Equal(DangerousOpDetector.Bash, category);
    }

    [Fact]
    public void Category_BashWithGitPush_IsGitPush()
    {
        var category = DangerousOpDetector.Category("bash", "{\"command\":\"git push origin\"}", null, "C:/project");
        Assert.Equal(DangerousOpDetector.GitPush, category);
    }

    // --- Outside-cwd detection ---

    [Theory]
    [InlineData("C:/project-other/file.txt", "C:/project")]
    [InlineData("C:/project2/file.txt", "C:/project")]
    [InlineData("C:/project/../secret.txt", "C:/project")]
    [InlineData("D:/other/file.txt", "C:/project")]
    public void IsOutsideCwd_DetectsEscapes(string absolutePath, string cwd)
    {
        Assert.True(DangerousOpDetector.IsOutsideCwd(absolutePath, cwd));
    }

    [Theory]
    [InlineData("C:/project/file.txt", "C:/project")]
    [InlineData("C:/project/sub/file.txt", "C:/project")]
    [InlineData("C:/project", "C:/project")]
    public void IsOutsideCwd_InsideCwd_IsFalse(string absolutePath, string cwd)
    {
        Assert.False(DangerousOpDetector.IsOutsideCwd(absolutePath, cwd));
    }

    [Fact]
    public void IsOutsideCwd_NullCwd_IsOutside()
    {
        Assert.True(DangerousOpDetector.IsOutsideCwd("C:/anything/file.txt", null));
    }

    // --- CategoryAsync with a fake file system ---

    [Fact]
    public async Task CategoryAsync_WriteInsideCwd_None()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { path = "notes/new.txt" });

        var category = await DangerousOpDetector.CategoryAsync("write", args, env);

        Assert.Equal(DangerousOpDetector.None, category);
    }

    [Fact]
    public async Task CategoryAsync_WriteOverExistingFile_WriteOverwrite()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        env.AddExistingFile("C:/project/notes/existing.txt");
        var args = JsonSerializer.SerializeToElement(new { path = "notes/existing.txt" });

        var category = await DangerousOpDetector.CategoryAsync("write", args, env);

        Assert.Equal(DangerousOpDetector.WriteOverwrite, category);
    }

    [Fact]
    public async Task CategoryAsync_WriteOutsideCwd_WriteOutsideCwd()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { path = "../outside.txt" });

        var category = await DangerousOpDetector.CategoryAsync("write", args, env);

        Assert.Equal(DangerousOpDetector.WriteOutsideCwd, category);
    }

    [Fact]
    public async Task CategoryAsync_EditOutsideCwd_WriteOutsideCwd()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { path = "C:/elsewhere/file.txt" });

        var category = await DangerousOpDetector.CategoryAsync("edit", args, env);

        Assert.Equal(DangerousOpDetector.WriteOutsideCwd, category);
    }

    [Fact]
    public async Task CategoryAsync_EditInsideCwd_None_EvenWhenExisting()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        env.AddExistingFile("C:/project/notes/file.txt");
        var args = JsonSerializer.SerializeToElement(new { path = "notes/file.txt" });

        var category = await DangerousOpDetector.CategoryAsync("edit", args, env);

        Assert.Equal(DangerousOpDetector.None, category);
    }

    [Fact]
    public async Task CategoryAsync_ReadTool_None()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { path = "anywhere.txt" });

        var category = await DangerousOpDetector.CategoryAsync("read", args, env);

        Assert.Equal(DangerousOpDetector.None, category);
    }

    [Fact]
    public async Task CategoryAsync_UnlistedTool_IsUnknown()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { });

        var category = await DangerousOpDetector.CategoryAsync("my-tool", args, env);

        Assert.Equal(DangerousOpDetector.Unknown, category);
    }

    [Fact]
    public async Task CategoryAsync_ReadTool_IsNone()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { path = "anywhere.txt" });

        var category = await DangerousOpDetector.CategoryAsync("read", args, env);

        Assert.Equal(DangerousOpDetector.None, category);
    }

    [Fact]
    public async Task CategoryAsync_UnknownToolWithInsidePath_IsUnknown()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { path = "notes/new.txt" });

        var category = await DangerousOpDetector.CategoryAsync("my-tool", args, env);

        Assert.Equal(DangerousOpDetector.Unknown, category);
    }

    [Fact]
    public async Task CategoryAsync_UnknownToolWithOutsidePath_IsWriteOutsideCwd()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { path = "../outside.txt" });

        var category = await DangerousOpDetector.CategoryAsync("my-tool", args, env);

        Assert.Equal(DangerousOpDetector.WriteOutsideCwd, category);
    }

    [Fact]
    public async Task CategoryAsync_McpToolWithCommand_IsMcpSpawn()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { command = "run.sh" });

        var category = await DangerousOpDetector.CategoryAsync("mcp.fileserver.exec", args, env);

        Assert.Equal(DangerousOpDetector.McpSpawn, category);
    }

    [Fact]
    public async Task CategoryAsync_McpToolWithExecArg_IsMcpSpawn()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { exec = "node server.js" });

        var category = await DangerousOpDetector.CategoryAsync("mcp.registry.run", args, env);

        Assert.Equal(DangerousOpDetector.McpSpawn, category);
    }

    [Fact]
    public async Task CategoryAsync_McpToolWithoutCommand_IsUnknown()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { query = "q" });

        var category = await DangerousOpDetector.CategoryAsync("mcp.fileserver.read", args, env);

        Assert.Equal(DangerousOpDetector.Unknown, category);
    }

    [Fact]
    public async Task CategoryAsync_UnlistedToolWithCommandArg_IsUnknown()
    {
        // A generic custom tool carrying a command argument is still unclassifiable
        // (Ask posture) — only MCP-prefixed tools escalate to McpSpawn.
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { command = "my-custom-runner" });

        var category = await DangerousOpDetector.CategoryAsync("my-runner", args, env);

        Assert.Equal(DangerousOpDetector.Unknown, category);
    }

    [Fact]
    public void Category_UnknownToolEmptyArgs_IsUnknown()
    {
        var category = DangerousOpDetector.Category("some-extension-tool", "{}", null, "C:/project");
        Assert.Equal(DangerousOpDetector.Unknown, category);
    }

    [Fact]
    public void Category_KnownSafeReadTool_IsNone()
    {
        var category = DangerousOpDetector.Category("read", "{\"path\":\"a.txt\"}", null, "C:/project");
        Assert.Equal(DangerousOpDetector.None, category);
    }

    [Fact]
    public void Category_McpSpawnSyncPath_IsMcpSpawn()
    {
        var category = DangerousOpDetector.Category("mcp.tools.exec", "{\"command\":\"npm run x\"}", null, "C:/project");
        Assert.Equal(DangerousOpDetector.McpSpawn, category);
    }

    [Fact]
    public async Task CategoryAsync_NullFileSystem_ClassifiesWithoutPathProbe()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "C:/project/file.txt" });

        var category = await DangerousOpDetector.CategoryAsync("write", args, null);

        Assert.Equal(DangerousOpDetector.None, category);
    }
}
