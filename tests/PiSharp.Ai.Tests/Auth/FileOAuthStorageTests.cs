using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class FileOAuthStorageTests
{
    [Fact]
    public async Task ConcurrentSetTokenAsyncWritesForDifferentProvidersAreNotLost()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        try
        {
            var storage = new FileOAuthStorage(path);
            var writes = Enumerable.Range(0, 20).SelectMany(round => new[]
            {
                storage.SetTokenAsync($"provider-a-{round}", $"token-a-{round}"),
                storage.SetTokenAsync($"provider-b-{round}", $"token-b-{round}")
            });
            await Task.WhenAll(writes);

            // Throws if the file was left torn by a non-atomic write.
            var json = await File.ReadAllTextAsync(path);
            using var document = JsonDocument.Parse(json);
            var providers = document.RootElement.GetProperty("providers");
            for (var round = 0; round < 20; round++)
            {
                Assert.Equal($"token-a-{round}", providers.GetProperty($"provider-a-{round}").GetProperty("token").GetString());
                Assert.Equal($"token-b-{round}", providers.GetProperty($"provider-b-{round}").GetProperty("token").GetString());
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GetTokenAsyncReturnsNullAndLogsWhenStoreIsCorrupt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        var messages = new ConcurrentQueue<string>();
        try
        {
            await File.WriteAllTextAsync(path, "{\"providers\": { \"openai\": { \"token\": \"trunc");

            var storage = new FileOAuthStorage(path, new RecordingLogger(messages));
            var token = await storage.GetTokenAsync("openai");

            Assert.Null(token);
            Assert.NotEmpty(messages);
            Assert.Contains(messages, m => m.Contains("corrupt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SetTokenAsyncRecoversFromCorruptStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        var messages = new ConcurrentQueue<string>();
        try
        {
            await File.WriteAllTextAsync(path, "{\"providers\": { \"openai\": { \"token\": \"trunc");

            var storage = new FileOAuthStorage(path, new RecordingLogger(messages));
            await storage.SetTokenAsync("openai", "fresh-token");

            Assert.Equal("fresh-token", await storage.GetTokenAsync("openai"));
            Assert.NotEmpty(messages);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}
