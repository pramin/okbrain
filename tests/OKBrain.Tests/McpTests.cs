using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OKBrain.Core;

namespace OKBrain.Tests;

public class McpTests : BundleFixture
{
    [Test]
    public void ReadAndEditsPreserveUtf8Bom()
    {
        Write("index.md", "\uFEFF# Index\n");
        Assert.That(BrainWrites.Read(Root, "index.md").Text, Is.EqualTo("\uFEFF# Index\n"));
        Assert.That(BrainWrites.Append(Root, "index.md", "more\n").Success, Is.True);
        Assert.That(File.ReadAllBytes(Path.Combine(Root, "index.md")), Is.EqualTo(Encoding.UTF8.GetBytes("\uFEFF# Index\nmore\n")));
        Assert.That(BrainWrites.Replace(Root, "index.md", "# Replaced\n").Success, Is.True);
        Assert.That(File.ReadAllBytes(Path.Combine(Root, "index.md")), Is.EqualTo(Encoding.UTF8.GetBytes("\uFEFF# Replaced\n")));
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private Process Start(string address, string? bundle, string? home = null)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        info.ArgumentList.Add(typeof(OKBrain.Tool.Program).Assembly.Location);
        info.ArgumentList.Add("serve"); info.ArgumentList.Add(address);
        if (bundle != null) { info.ArgumentList.Add("--bundle"); info.ArgumentList.Add(bundle); }
        if (home != null) info.Environment["HOME"] = home;
        return Process.Start(info)!;
    }

    [Test]
    public async Task StreamableHttpListsAndCallsToolsAndStops()
    {
        Write("index.md", "# Index\n");
        int port = FreePort();
        using var process = Start($"http://127.0.0.1:{port}", Root);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            HttpResponseMessage? init = null;
            for (int i = 0; i < 100 && init == null; i++)
            {
                try { init = await Send(client, null, "initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }); }
                catch (HttpRequestException) { await Task.Delay(50); }
            }
            Assert.That(init, Is.Not.Null);
            var session = init!.Headers.GetValues("Mcp-Session-Id").Single();
            Assert.That((await Body(init)).GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString(), Is.Not.Empty);
            var list = await Body(await Send(client, session, "tools/list", new { }));
            var names = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray();
            Assert.That(names, Does.Contain("validate").And.Contain("move").And.Contain("multi_edit").And.Contain("neighbors"));
            var stats = await Call(client, session, "stats", new { });
            Assert.That(stats.GetProperty("isError").GetBoolean(), Is.False);
            var appended = await Call(client, session, "append", new { path = "index.md", text = "\nHello MCP\n" });
            Assert.That(appended.GetProperty("isError").GetBoolean(), Is.False);
            var read = await Call(client, session, "read", new { path = "index.md" });
            Assert.That(read.GetProperty("content")[0].GetProperty("text").GetString(), Does.Contain("Hello MCP"));
            var invalid = await Call(client, session, "create", new { path = "bad.md", text = "invalid" });
            Assert.That(invalid.GetProperty("isError").GetBoolean(), Is.True);
            Assert.That(File.Exists(Path.Combine(Root, "bad.md")), Is.False);
            var missing = await Call(client, session, "read", new { path = "missing.md" });
            Assert.That(missing.GetProperty("isError").GetBoolean(), Is.True);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            Assert.ThrowsAsync<TaskCanceledException>(async () => await client.GetAsync("/", cts.Token));
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/");
            request.Headers.Add("Mcp-Session-Id", session);
            await client.SendAsync(request);
        }
        finally
        {
            if (!process.HasExited)
            {
                using var signal = Process.Start("kill", $"-INT {process.Id}")!;
                await signal.WaitForExitAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(true); Assert.Fail("Server failed to shut down."); }
            }
        }
        Assert.That(process.ExitCode, Is.EqualTo(0));
    }

    private static int requestId;
    private static async Task<HttpResponseMessage> Send(HttpClient client, string? session, string method, object parameters, CancellationToken cancellationToken = default, int? id = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (session != null) request.Headers.Add("Mcp-Session-Id", session);
        request.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id ?? Interlocked.Increment(ref requestId), method, @params = parameters }), Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode(); return response;
    }
    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (text.StartsWith("event:")) text = text.Split('\n').First(line => line.StartsWith("data: "))[6..];
        return JsonDocument.Parse(text).RootElement.Clone();
    }
    private static async Task<JsonElement> Call(HttpClient client, string session, string name, object arguments) =>
        (await Body(await Send(client, session, "tools/call", new { name, arguments }))).GetProperty("result");

    private static async Task<string> Initialize(HttpClient client)
    {
        for (int i = 0; i < 200; i++)
        {
            try
            {
                var response = await Send(client, null, "initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
                return response.Headers.GetValues("Mcp-Session-Id").Single();
            }
            catch (HttpRequestException) { await Task.Delay(50); }
        }
        throw new TimeoutException("Server did not start.");
    }

    private static JsonElement Value(JsonElement result) => JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();

    [Test]
    public async Task ProjectBrainIsPerSessionLayeredAndWritesNeedScope()
    {
        Write("index.md", "# Common\n");
        Write("shared.md", "# Shared common\n");
        var project = Path.Combine(Root + "-proj");
        Directory.CreateDirectory(Path.Combine(project, ".brain"));
        File.WriteAllText(Path.Combine(project, ".brain", "index.md"), "# Project\n");
        File.WriteAllText(Path.Combine(project, ".brain", "shared.md"), "# Shared project\n");
        int port = FreePort();
        using var process = Start($"http://127.0.0.1:{port}", Root);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var a = await Initialize(client);
            var b = await Initialize(client);
            var c = await Initialize(client);
            var direct = await Call(client, c, "set_project", new { path = Path.Combine(project, ".brain") });
            Assert.That(direct.GetProperty("isError").GetBoolean(), Is.False, direct.ToString());
            Assert.That(Value(direct).GetProperty("project").GetString(), Is.EqualTo(Path.Combine(project, ".brain")));
            Assert.That((await Call(client, c, "set_project", new { path = Path.Combine(project, "nope") })).GetProperty("isError").GetBoolean(), Is.True);
            Assert.That((await Call(client, c, "set_project", new { path = Root })).GetProperty("isError").GetBoolean(), Is.True);
            var set = await Call(client, a, "set_project", new { path = project });
            Assert.That(set.GetProperty("isError").GetBoolean(), Is.False, set.ToString());
            var fromProject = Value(await Call(client, a, "read", new { path = "shared.md" }));
            Assert.That(fromProject.GetProperty("source").GetString(), Is.EqualTo("project"));
            Assert.That(fromProject.GetProperty("text").GetString(), Does.Contain("project"));
            File.Delete(Path.Combine(project, ".brain", "shared.md"));
            var fallback = Value(await Call(client, a, "read", new { path = "shared.md" }));
            Assert.That(fallback.GetProperty("source").GetString(), Is.EqualTo("common"));
            Assert.That(Value(await Call(client, a, "search", new { query = "Project" })).EnumerateArray().Select(x => x.GetProperty("source").GetString()), Does.Contain("project"));
            Assert.That(Value(await Call(client, b, "read", new { path = "index.md" })).GetProperty("source").GetString(), Is.EqualTo("common"));
            var noScope = await Call(client, a, "append", new { path = "index.md", text = "x\n" });
            Assert.That(noScope.GetProperty("isError").GetBoolean(), Is.True);
            Assert.That((await Call(client, a, "append", new { path = "index.md", text = "x\n", scope = "project" })).GetProperty("isError").GetBoolean(), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(project, ".brain", "index.md")), Does.Contain("x\n"));
            Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Does.Not.Contain("x\n"));
            Assert.That((await Call(client, b, "append", new { path = "index.md", text = "y\n" })).GetProperty("isError").GetBoolean(), Is.False);
            Assert.That((await Call(client, b, "read", new { path = "index.md", scope = "project" })).GetProperty("isError").GetBoolean(), Is.True);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } Directory.Delete(project, true); }
    }

    [Test]
    public async Task AllEditingAndGraphToolsWorkThroughMcp()
    {
        Write("index.md", "# Index\n");
        int port = FreePort();
        using var process = Start($"http://127.0.0.1:{port}", Root);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var session = await Initialize(client);
            async Task<JsonElement> Ok(string name, object args)
            {
                var result = await Call(client, session, name, args);
                Assert.That(result.GetProperty("isError").GetBoolean(), Is.False, $"{name}: {result}");
                return Value(result);
            }
            const string initial = "---\ntype: concept\ntitle: Alpha\n---\n# Alpha\n";
            await Ok("create", new { path = "a.md", text = initial });
            Assert.That((await Ok("read", new { path = "a.md" })).GetProperty("text").GetString(), Is.EqualTo(initial));
            await Ok("replace", new { path = "a.md", text = initial + "First\n" });
            await Ok("exact_edit", new { path = "a.md", oldText = "First", newText = "Second" });
            await Ok("append", new { path = "a.md", text = "End\n" });
            await Ok("prepend", new { path = "a.md", text = "Intro\n" });
            await Ok("insert", new { path = "a.md", anchor = "End", text = "Before ", after = false });
            await Ok("line_edit", new { path = "a.md", startLine = 6, endLine = 6, expected = "# Alpha\n", text = "# Changed\n" });
            await Ok("multi_edit", new { path = "a.md", edits = new[] { new { oldText = "Second", newText = "Third" }, new { oldText = "Intro", newText = "Start" } } });
            await Ok("frontmatter_set", new { path = "a.md", key = "custom", yamlValue = "value" });
            Assert.That(File.ReadAllText(Path.Combine(Root, "a.md")), Does.Contain("custom: value"));
            await Ok("frontmatter_remove", new { path = "a.md", key = "custom" });
            var before = File.ReadAllText(Path.Combine(Root, "a.md"));
            Assert.That(before, Does.Contain("Start\n# Changed\nThird\nBefore End\n").And.Not.Contain("custom:"));
            var rejected = await Call(client, session, "multi_edit", new { path = "a.md", edits = new[] { new { oldText = "Third", newText = "Changed" }, new { oldText = "missing", newText = "x" } } });
            Assert.That(rejected.GetProperty("isError").GetBoolean(), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(Root, "a.md")), Is.EqualTo(before));
            await Ok("make_directory", new { path = "folder" });
            await Ok("copy", new { from = "a.md", to = "folder/copy.md" });
            Assert.That(File.ReadAllText(Path.Combine(Root, "folder/copy.md")), Is.EqualTo(before));
            await Ok("move", new { from = "folder/copy.md", to = "folder/moved.md" });
            Assert.That(File.Exists(Path.Combine(Root, "folder/moved.md")), Is.True);
            await Ok("append", new { path = "a.md", text = "[Moved](folder/moved.md)\n" });
            Assert.That((await Ok("backlinks", new { path = "folder/moved.md" })).EnumerateArray().Any(x => x.GetProperty("source").GetString() == "a.md"), Is.True);
            Assert.That((await Ok("neighbors", new { path = "a.md", direction = "out" })).EnumerateArray().Any(x => x.GetProperty("path").GetString() == "folder/moved.md"), Is.True);
            Assert.That((await Ok("export", new { })).GetProperty("edges").GetArrayLength(), Is.GreaterThan(0));
            Assert.That((await Ok("relations", new { })).GetProperty("links-to").GetInt32(), Is.GreaterThan(0));
            Assert.That((await Ok("findings", new { })).GetProperty("brokenLinks").GetArrayLength(), Is.Zero);
            await Ok("delete", new { path = "folder/moved.md" });
            Assert.That(File.Exists(Path.Combine(Root, "folder/moved.md")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(Root, "a.md")), Does.Contain("Moved").And.Not.Contain("folder/moved.md"));
            // Move/delete can create a generated directory index.
            if (File.Exists(Path.Combine(Root, "folder/index.md"))) await Ok("delete", new { path = "folder/index.md" });
            await Ok("remove_directory", new { path = "folder" });
            Assert.That(Directory.Exists(Path.Combine(Root, "folder")), Is.False);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Test]
    public async Task ServeUsesDefaultBundleUnderChildHomeAndDoesNotCreateMissingBundle()
    {
        EnsureBundle();
        var home = Path.Combine(Root, "home"); Directory.CreateDirectory(home);
        int port = FreePort();
        using (var missing = Start($"http://127.0.0.1:{port}", null, home))
        {
            await missing.WaitForExitAsync();
            Assert.That(missing.ExitCode, Is.Not.Zero);
            Assert.That(await missing.StandardError.ReadToEndAsync(), Does.Contain("bundle.not-found"));
            Assert.That(Directory.Exists(Path.Combine(home, ".brain")), Is.False);
        }
        Directory.CreateDirectory(Path.Combine(home, ".brain"));
        File.WriteAllText(Path.Combine(home, ".brain", "index.md"), "# Home bundle\n");
        using var process = Start($"http://127.0.0.1:{port}", null, home);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        try
        {
            var session = await Initialize(client);
            var read = await Call(client, session, "read", new { path = "index.md" });
            Assert.That(Value(read).GetProperty("text").GetString(), Is.EqualTo("# Home bundle\n"));
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Test]
    public async Task CancelledInFlightWriteDoesNotCommit()
    {
        Write("index.md", "# Index\n");
        int port = FreePort();
        using var process = Start($"http://127.0.0.1:{port}", Root);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(120) };
        try
        {
            var session = await Initialize(client);
            for (int i = 0; i < 5000; i++) Write($"docs/{i:D5}.md", "---\ntype: concept\n---\n# Document\n");
            using var cts = new CancellationTokenSource();
            var pending = Send(client, session, "tools/call", new { name = "reindex", arguments = new { } }, cts.Token, id: 1000000);
            await Task.Delay(100);
            Assert.That(pending.IsCompleted, Is.False, "The call must be in flight before cancellation.");
            using var notification = new HttpRequestMessage(HttpMethod.Post, "/");
            notification.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            notification.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            notification.Headers.Add("Mcp-Session-Id", session);
            notification.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 1000000, reason = "test" } }), Encoding.UTF8, "application/json");
            using var notified = await client.SendAsync(notification);
            notified.EnsureSuccessStatusCode();
            cts.Cancel();
            Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);
            // A second write waits on the same gate, so an ignored cancellation would
            // finish reindexing before this barrier returns and change the index.
            var barrier = await Call(client, session, "append", new { path = "index.md", text = "" });
            Assert.That(barrier.GetProperty("isError").GetBoolean(), Is.False, barrier.ToString());
            Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Is.EqualTo("# Index\n"));
            Assert.That(File.Exists(Path.Combine(Root, "docs/index.md")), Is.False);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Test]
    public void EditingIsGatedAndAtomic()
    {
        Write("index.md", "# Index\n");
        Assert.That(BrainWrites.Create(Root, "bad.md", "bad").Success, Is.False);
        Assert.That(File.Exists(Path.Combine(Root, "bad.md")), Is.False);
        Assert.That(BrainWrites.Append(Root, "index.md", "hello hello\n").Success, Is.True);
        Assert.Throws<BundleException>(() => BrainWrites.MultiEdit(Root, "index.md", [new("# Index", "# Changed"), new("hello", "x")]));
        Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Does.StartWith("# Index"));
        Assert.That(BrainWrites.MakeDirectory(Root, "empty").Success, Is.True);
        Assert.That(BrainWrites.RemoveDirectory(Root, "empty").Success, Is.True);
    }

    [Test]
    public void FrontmatterAndTextEditsPreserveOtherContent()
    {
        Write("index.md", "# Index\n");
        Write("a.md", "---\ntype: concept\ntitle: Original # keep comment\ntags: [one, two]\n---\n# Body\n");
        Assert.That(BrainWrites.FrontmatterSet(Root, "a.md", "title", "Updated").Success, Is.True);
        Assert.That(File.ReadAllText(Path.Combine(Root, "a.md")), Does.Contain("title: Updated # keep comment\ntags: [one, two]"));
        Assert.That(BrainWrites.FrontmatterSet(Root, "a.md", "custom", "value").Success, Is.True);
        Assert.That(BrainWrites.FrontmatterRemove(Root, "a.md", "custom").Success, Is.True);
        Assert.That(BrainWrites.Prepend(Root, "a.md", "Intro\n").Success, Is.True);
        Assert.That(File.ReadAllText(Path.Combine(Root, "a.md")), Does.Contain("---\nIntro\n# Body"));
    }

    [Test]
    public async Task InvalidAddressAndBindFailureAreReported()
    {
        EnsureBundle();
        using var invalid = Start("not-an-address", Root);
        await invalid.WaitForExitAsync();
        Assert.That(invalid.ExitCode, Is.EqualTo(2));
        Assert.That(await invalid.StandardError.ReadToEndAsync(), Does.Contain("input.arguments"));
        var port = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        try
        {
            using var bound = Start($"http://127.0.0.1:{port}", Root);
            await bound.WaitForExitAsync();
            Assert.That(bound.ExitCode, Is.EqualTo(1));
            Assert.That(await bound.StandardError.ReadToEndAsync(), Does.Contain("server.start"));
        }
        finally { listener.Stop(); }
    }
}
