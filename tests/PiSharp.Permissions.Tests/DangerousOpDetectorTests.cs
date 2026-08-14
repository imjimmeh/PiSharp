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
    [InlineData("rm -rf /tmp/build")]
    public void BashCategoryOf_DestructiveCommands_ClassifyGitPush(string command)
    {
        Assert.Equal(DangerousOpDetector.GitPush, DangerousOpDetector.BashCategoryOf(command));
        Assert.NotEqual(DangerousOpDetector.Bash, DangerousOpDetector.BashCategoryOf(command));
    }

    [Theory]
    [InlineData("echo hello")]
    [InlineData("git status")]
    [InlineData("npm test")]
    public void BashCategoryOf_OrdinaryCommands_ClassifyBash(string command)
    {
        Assert.Equal(DangerousOpDetector.Bash, DangerousOpDetector.BashCategoryOf(command));
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
    public async Task CategoryAsync_UnlistedTool_None()
    {
        var env = new FakeExecutionEnv { Cwd = "C:/project" };
        var args = JsonSerializer.SerializeToElement(new { });

        var category = await DangerousOpDetector.CategoryAsync("my-tool", args, env);

        Assert.Equal(DangerousOpDetector.None, category);
    }

    [Fact]
    public async Task CategoryAsync_NullFileSystem_ClassifiesWithoutPathProbe()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "C:/project/file.txt" });

        var category = await DangerousOpDetector.CategoryAsync("write", args, null);

        Assert.Equal(DangerousOpDetector.None, category);
    }
}
