using System.Net;
using Brainbox.Core.Caching;
using Brainbox.Core.Translation;
using Xunit;

namespace Brainbox.Core.Tests;

public class OpenAiCompatibleProviderTests
{
    [Theory]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/v1")]
    [InlineData("http://localhost:1234", "http://localhost:1234/v1")]
    [InlineData("localhost:1234/v1/", "http://localhost:1234/v1")]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1")]
    [InlineData("http://127.0.0.1:1234/v1/models", "http://127.0.0.1:1234/v1")]
    public void Normalizes_endpoint(string input, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleProvider.NormalizeBaseUrl(input));
    }

    [Theory]
    [InlineData("{\"translations\":[\"Hello\",\"World\"]}")]
    [InlineData("```json\n{\"translations\": [\"Hello\", \"World\"]}\n```")]
    [InlineData("<think>The user wants...</think>\n{\"translations\":[\"Hello\",\"World\"]}")]
    [InlineData("[\"Hello\",\"World\"]")]
    [InlineData("Sure! Here you go: {\"result\":[\"Hello\",\"World\"]} Hope it helps")]
    [InlineData("1. Hello\n2. World")]
    [InlineData("Hello\nWorld")]
    public void Parses_reply_variants(string reply)
    {
        var parsed = OpenAiCompatibleProvider.ParseTranslations(reply, 2);
        Assert.NotNull(parsed);
        Assert.Equal(new[] { "Hello", "World" }, parsed);
    }

    [Fact]
    public void Wrong_count_is_rejected()
    {
        Assert.Null(OpenAiCompatibleProvider.ParseTranslations("{\"translations\":[\"only one\"]}", 2));
    }

    [Fact]
    public void Prompt_contains_context_and_numbered_segments()
    {
        var batch = new TranslationBatch(new[] { "行こう", "早く！" }, "en", "ja",
            new[] { new ContextEntry("ユキ：待って", "Yuki: Wait"), new ContextEntry("駅の前", null) });
        var (system, user) = OpenAiCompatibleProvider.BuildPrompt(batch);
        Assert.Contains("Japanese", system);
        Assert.Contains("English", system);
        Assert.Contains("exactly 2 strings", system);
        Assert.Contains("CONTEXT", user);
        Assert.Contains("ユキ：待って  =>  Yuki: Wait", user);
        Assert.Contains("1. 行こう", user);
        Assert.Contains("2. 早く！", user);
    }

    [Fact]
    public async Task Auto_picks_loaded_model_skipping_embeddings_and_translates_batch()
    {
        var server = new FakeOpenAiServer { ReplyFor = _ => "{\"translations\":[\"Let's go\",\"Hurry!\"]}" };
        using var p = new OpenAiCompatibleProvider("LM Studio", "http://localhost:1234", model: null, handler: server);
        var result = await p.TranslateAsync(new TranslationBatch(new[] { "行こう", "早く！" }, "en", null, Array.Empty<ContextEntry>()), CancellationToken.None);
        Assert.Equal(new[] { "Let's go", "Hurry!" }, result);
        Assert.Equal("qwen2.5-7b-instruct", p.ActiveModel);
        Assert.Single(server.ChatRequests);
        Assert.Equal("qwen2.5-7b-instruct", server.ChatRequests[0].RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Uses_configured_model()
    {
        var server = new FakeOpenAiServer { ReplyFor = _ => "{\"translations\":[\"Hi\"]}" };
        using var p = new OpenAiCompatibleProvider("Ollama", "http://localhost:11434/v1", model: "llama3.1:8b", handler: server);
        await p.TranslateAsync(new TranslationBatch(new[] { "こんにちは" }, "en", null, Array.Empty<ContextEntry>()), CancellationToken.None);
        Assert.Equal("llama3.1:8b", server.ChatRequests[0].RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Falls_back_to_per_line_when_structure_is_lost()
    {
        var calls = 0;
        var server = new FakeOpenAiServer
        {
            ReplyFor = user =>
            {
                calls++;
                if (user.Contains("SEGMENTS (2)", StringComparison.Ordinal)) return "I translated them: Hello and World";
                return user.Contains("こんにちは", StringComparison.Ordinal) ? "Hello" : "World";
            },
        };
        using var p = new OpenAiCompatibleProvider("LM Studio", "http://localhost:1234/v1", "m", handler: server);
        var r = await p.TranslateAsync(new TranslationBatch(new[] { "こんにちは", "世界" }, "en", null, Array.Empty<ContextEntry>()), CancellationToken.None);
        Assert.Equal(new[] { "Hello", "World" }, r);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Unreachable_server_raises_provider_exception()
    {
        var server = new FakeOpenAiServer { Refuse = true };
        using var p = new OpenAiCompatibleProvider("LM Studio", "http://localhost:1234/v1", "m", handler: server);
        var ex = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            p.TranslateAsync(new TranslationBatch(new[] { "x" }, "en", null, Array.Empty<ContextEntry>()), CancellationToken.None));
        Assert.Contains("not reachable", ex.Message);
    }

    [Fact]
    public async Task Http_error_raises_provider_exception()
    {
        var server = new FakeOpenAiServer { Status = HttpStatusCode.InternalServerError };
        using var p = new OpenAiCompatibleProvider("LM Studio", "http://localhost:1234/v1", "m", handler: server);
        await Assert.ThrowsAsync<TranslationProviderException>(() =>
            p.TranslateAsync(new TranslationBatch(new[] { "x" }, "en", null, Array.Empty<ContextEntry>()), CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_raises_provider_exception()
    {
        var server = new FakeOpenAiServer { Delay = TimeSpan.FromSeconds(5) };
        using var p = new OpenAiCompatibleProvider("LM Studio", "http://localhost:1234/v1", "m", timeout: TimeSpan.FromMilliseconds(200), handler: server);
        var ex = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            p.TranslateAsync(new TranslationBatch(new[] { "x" }, "en", null, Array.Empty<ContextEntry>()), CancellationToken.None));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task No_model_loaded_is_reported()
    {
        var server = new FakeOpenAiServer();
        server.Models.Clear();
        using var p = new OpenAiCompatibleProvider("LM Studio", "http://localhost:1234/v1", null, handler: server);
        var ex = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            p.TranslateAsync(new TranslationBatch(new[] { "x" }, "en", null, Array.Empty<ContextEntry>()), CancellationToken.None));
        Assert.Contains("no model", ex.Message);
    }

    [Fact]
    public async Task Lists_models()
    {
        using var p = new OpenAiCompatibleProvider("LM Studio", "http://localhost:1234/v1", null, handler: new FakeOpenAiServer());
        var models = await p.ListModelsAsync(CancellationToken.None);
        Assert.Contains("qwen2.5-7b-instruct", models);
    }
}

public class ResilientTranslatorTests
{
    private static TranslationBatch Batch(params string[] lines) => new(lines, "en", null, Array.Empty<ContextEntry>());

    [Fact]
    public async Task Falls_back_to_next_provider_and_reports_degraded()
    {
        var clock = new FakeClock();
        var local = new FakeProvider("LM Studio") { Down = true };
        var cloud = new FakeProvider("Google", supportsContext: false);
        var chain = new ResilientTranslator(new ITranslationProvider[] { local, cloud }, clock.Read);
        var outcome = await chain.TranslateAsync(Batch("a"), CancellationToken.None);
        Assert.True(outcome.Success);
        Assert.Equal("Google", outcome.ProviderName);
        Assert.Equal(ProviderHealth.Degraded, chain.Status.Health);
    }

    [Fact]
    public async Task Circuit_breaker_backs_off_then_retries()
    {
        var clock = new FakeClock();
        var local = new FakeProvider("LM Studio") { Down = true };
        var chain = new ResilientTranslator(new[] { local }, clock.Read);

        Assert.False((await chain.TranslateAsync(Batch("a"), CancellationToken.None)).Success);
        Assert.Equal(ProviderHealth.Unavailable, chain.Status.Health);
        Assert.Single(local.Requests);

        // Within the back-off window the provider is not hammered.
        clock.Advance(500);
        Assert.False((await chain.TranslateAsync(Batch("a"), CancellationToken.None)).Success);
        Assert.Single(local.Requests);

        // After the back-off it is retried and recovers.
        local.Down = false;
        clock.Advance(2000);
        var ok = await chain.TranslateAsync(Batch("a"), CancellationToken.None);
        Assert.True(ok.Success);
        Assert.Equal(ProviderHealth.Healthy, chain.Status.Health);
    }

    [Fact]
    public void Backoff_grows_exponentially_and_caps()
    {
        var clock = new FakeClock();
        var b = new CircuitBreaker(clock.Read, 2000, 60000);
        b.RecordFailure("x");
        Assert.Equal(clock.Now + 2000, b.RetryAtMs);
        b.RecordFailure("x");
        Assert.Equal(clock.Now + 4000, b.RetryAtMs);
        for (var i = 0; i < 20; i++) b.RecordFailure("x");
        Assert.Equal(clock.Now + 60000, b.RetryAtMs);
        b.RecordSuccess();
        Assert.False(b.IsOpen);
    }

    [Fact]
    public async Task Context_is_stripped_for_providers_that_cannot_use_it()
    {
        var cloud = new FakeProvider("Google", supportsContext: false);
        var chain = new ResilientTranslator(new[] { cloud }, new FakeClock().Read);
        await chain.TranslateAsync(new TranslationBatch(new[] { "a" }, "en", null, new[] { new ContextEntry("ctx", null) }), CancellationToken.None);
        Assert.Empty(cloud.Requests[0].Context);
    }

    [Fact]
    public async Task No_providers_is_a_clean_failure()
    {
        var chain = new ResilientTranslator(Array.Empty<ITranslationProvider>(), new FakeClock().Read);
        var r = await chain.TranslateAsync(Batch("a"), CancellationToken.None);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Delegate_provider_translates_each_line()
    {
        var p = new DelegateTranslationProvider("Per-line", false, (t, target, src, ct) => Task.FromResult(t.ToUpperInvariant() + "@" + target));
        var r = await p.TranslateAsync(Batch("a", "b"), CancellationToken.None);
        Assert.Equal(new[] { "A@en", "B@en" }, r);
    }
}

public class ContextManagerTests
{
    [Fact]
    public void Keeps_recent_history_and_nearby_text_without_duplicates()
    {
        var c = new ContextManager(3);
        c.Record("一", "one");
        c.Record("二", "two");
        c.Record("三", "three");
        c.Record("四", "four");
        c.Record("二", "two"); // re-seen: moves to newest
        var ctx = c.Build(new[] { "五" }, new[] { "隣", "四" });
        Assert.Equal(new[] { "三", "四", "二", "隣" }, ctx.Select(e => e.Source));
        Assert.Equal("two", ctx[2].Translation);
        Assert.Null(ctx[3].Translation);
    }

    [Fact]
    public void Excludes_lines_being_translated_and_respects_budget()
    {
        var c = new ContextManager(10) { MaxContextChars = 10 };
        c.Record("aaaaa", "AAAAA");
        c.Record("bbbbb", "BBBBB");
        var ctx = c.Build(new[] { "bbbbb" }, Array.Empty<string>());
        Assert.Single(ctx);
        Assert.Equal("aaaaa", ctx[0].Source);
        c.Record("ccccc", "CCCCC");
        Assert.Equal(new[] { "ccccc" }, c.Build(new[] { "zzz" }, Array.Empty<string>()).Select(e => e.Source));
    }

    [Fact]
    public void Zero_capacity_disables_context()
    {
        var c = new ContextManager(0);
        c.Record("a", "b");
        Assert.Empty(c.Build(new[] { "x" }, new[] { "y" }));
    }
}

public class TranslationCacheTests
{
    [Fact]
    public void Memory_cache_hits_after_put_and_normalizes()
    {
        var cache = new TranslationCache();
        Assert.False(cache.TryGet("こんにちは", "en", out _));
        cache.Put("こんにちは", "en", "Hello", "LM Studio");
        Assert.True(cache.TryGet("  こんにちは ", "en", out var v));
        Assert.Equal("Hello", v.Translation);
        Assert.False(cache.TryGet("こんにちは", "fr", out _));
        Assert.Equal(1, cache.Hits);
        Assert.Equal(2, cache.Misses);
    }

    [Fact]
    public void Lru_evicts_oldest()
    {
        var cache = new TranslationCache(memoryCapacity: 16);
        for (var i = 0; i < 20; i++) cache.Put("t" + i, "en", "T" + i, "p");
        Assert.Equal(16, cache.MemoryCount);
        Assert.False(cache.TryGet("t0", "en", out _));
        Assert.True(cache.TryGet("t19", "en", out _));
    }

    [Fact]
    public void Sqlite_store_persists_across_instances()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbx-cache-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "cache.db");
        try
        {
            using (var cache = new TranslationCache(new SqliteTranslationStore(path)))
            {
                cache.Put("おはよう", "en", "Good morning", "LM Studio");
                cache.Put("ありがとう", "en", "Thank you", "LM Studio");
                cache.Put("ありがとう", "en", "Thanks", "Ollama"); // upsert
                Assert.Equal(2, cache.PersistentCount);
            }

            using (var cache2 = new TranslationCache(new SqliteTranslationStore(path)))
            {
                Assert.True(cache2.TryGet("おはよう", "en", out var a));
                Assert.Equal("Good morning", a.Translation);
                Assert.True(cache2.TryGet("ありがとう", "en", out var b));
                Assert.Equal("Thanks", b.Translation);
                Assert.Equal("Ollama", b.Provider);
                cache2.Clear();
                Assert.Equal(0, cache2.PersistentCount);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Broken_store_does_not_break_cache()
    {
        var cache = new TranslationCache(new ThrowingStore());
        cache.Put("a", "en", "A", "p");
        Assert.True(cache.TryGet("a", "en", out var v));
        Assert.Equal("A", v.Translation);
    }

    private sealed class ThrowingStore : ITranslationStore
    {
        public long Count => throw new IOException("disk gone");
        public bool TryGet(string key, out CachedTranslation value) => throw new IOException("disk gone");
        public void Put(string key, string target, CachedTranslation value) => throw new IOException("disk gone");
        public void Clear() => throw new IOException("disk gone");
        public void Dispose() { }
    }
}
