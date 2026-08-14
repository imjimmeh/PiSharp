using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Git.Tests;

public sealed class ShareSlashCommandTests : IDisposable
{
    private sealed class FakeUploader(Func<GistUploadRequest, GistUploadResult> respond) : IGistUploader
    {
        public GistUploadRequest? LastRequest;
        private readonly Func<GistUploadRequest, GistUploadResult> _respond = respond;

        public Task<GistUploadResult> UploadAsync(GistUploadRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private readonly FakeUi _ui = new();
    private readonly List<string> _messages = [];
    private readonly string _tempDir;
    private readonly GitPluginOptions _options = new() { GithubAuthStoreProvider = "github" };
    private readonly FakeUploader _uploader;
    private readonly GistTokenResolver _resolver;
    private readonly ShareSlashCommand _command;

    public ShareSlashCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pisharp-share-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _uploader = new FakeUploader(_ => new GistUploadResult(true, null, "https://gist.github.com/user/abc", "abc", 5));
        var storage = new InMemoryOAuthStorage();
        storage.SetTokenAsync("github", "ghp_test").GetAwaiter().GetResult();
        _resolver = new GistTokenResolver(storage, _options);

        var host = new CommandHost(_ui, HasUi: true, _tempDir, (text, _) =>
        {
            _messages.Add(text);
            return Task.CompletedTask;
        });
        _command = new ShareSlashCommand(host, _uploader, _resolver, _options);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private string ShareFile(string name = "share.txt", string content = "hello")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task UploadsFileAndReportsUrl()
    {
        var path = ShareFile();
        ShareCompletedEvent? evt = null;
        _command.ShareCompleted += e => evt = e;

        await _command.HandleAsync(path);

        Assert.Equal(1, _ui.ConfirmCalls);
        Assert.NotNull(_uploader.LastRequest);
        Assert.Equal("share.txt", _uploader.LastRequest.FileName);
        Assert.Equal("hello", _uploader.LastRequest.Content);
        Assert.False(_uploader.LastRequest.IsPublic);
        Assert.Equal("ghp_test", _uploader.LastRequest.Token);
        Assert.Contains(_messages, m => m.Contains("https://gist.github.com/user/abc"));
        Assert.NotNull(evt);
        Assert.Equal("abc", evt.GistId);
    }

    [Fact]
    public async Task YesSkipsConfirmation()
    {
        var path = ShareFile();

        await _command.HandleAsync($"{path} --yes");

        Assert.Equal(0, _ui.ConfirmCalls);
        Assert.NotNull(_uploader.LastRequest);
    }

    [Fact]
    public async Task PublicFlagMakesGistPublic()
    {
        var path = ShareFile();

        await _command.HandleAsync($"{path} --public --yes");

        Assert.True(_uploader.LastRequest!.IsPublic);
    }

    [Fact]
    public async Task LocalFlagCopiesInsteadOfUploading()
    {
        var source = ShareFile("session.jsonl", "line1\nline2\n");
        var target = Path.Combine(_tempDir, "copy", "session.jsonl");

        await _command.HandleAsync($"{source} --local {target} --yes");

        Assert.Null(_uploader.LastRequest);
        Assert.True(File.Exists(target));
        Assert.Equal("line1\nline2\n", File.ReadAllText(target));
    }

    [Fact]
    public async Task PathOutsideAllowedRootsIsRejected()
    {
        var outside = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "pisharp-not-allowed.txt")
            : "/etc/pisharp-not-allowed.txt";

        await _command.HandleAsync(outside);

        Assert.Null(_uploader.LastRequest);
        Assert.Contains(_ui.Notifications, n => n.Message.Contains("must be under the session or temp directory"));
    }

    [Fact]
    public async Task MissingFileIsReported()
    {
        var missing = Path.Combine(_tempDir, "nope.txt");

        await _command.HandleAsync(missing);

        Assert.Null(_uploader.LastRequest);
        Assert.Contains(_ui.Notifications, n => n.Message.Contains("File not found"));
    }

    [Fact]
    public async Task NoArgFormIsPhaseGated()
    {
        await _command.HandleAsync("");

        Assert.Null(_uploader.LastRequest);
        Assert.Contains(_ui.Notifications, n => n.Message.Contains("pass a file path explicitly"));
    }

    [Fact]
    public async Task UploadFailureSurfacesErrorAndCancelsConfirmNext()
    {
        var failing = new FakeUploader(_ => new GistUploadResult(false, "gist limit reached", null, null, 0));
        var path = ShareFile();
        var host = new CommandHost(_ui, HasUi: true, _tempDir, (text, _) =>
        {
            _messages.Add(text);
            return Task.CompletedTask;
        });
        var command = new ShareSlashCommand(host, failing, _resolver, _options);

        await command.HandleAsync($"{path} --yes");

        Assert.Contains(_ui.Notifications, n => n.Message.Contains("gist limit reached"));
        Assert.DoesNotContain(_messages, m => m.Contains("Shared as gist"));
    }
}
