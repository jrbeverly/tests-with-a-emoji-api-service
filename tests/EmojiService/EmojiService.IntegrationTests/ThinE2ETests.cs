using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class ThinE2ETests : DynamoDbIntegrationTest
{
    public ThinE2ETests()
        : base("thin-e2e") { }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    );

    // ── Thin E2E: upload → consumer → sync → downstream resolve ──

    [Fact]
    public async Task ThinE2E_UploadSyncResolve_DownstreamReadsLocalManifest()
    {
        // 1. Upload several emoji with primary and secondary aliases
        var uidAlpha = await UploadEmojiAsync(
            "alpha",
            "Alpha Emoji",
            tags: ["reaction"],
            categories: ["fun"],
            owner: "team-a"
        );
        var uidBeta = await UploadEmojiAsync(
            "beta",
            "Beta Emoji",
            tags: ["reaction"],
            categories: ["work"],
            owner: "team-b"
        );
        var uidGamma = await UploadEmojiAsync(
            "gamma",
            "Gamma Emoji",
            tags: ["party"],
            owner: "team-a"
        );

        // Add a secondary alias to gamma
        var addAliasResp = await Client.PostAsJsonAsync(
            $"/api/v1/emoji/{uidGamma}/aliases",
            new { alias = "gamma-extra" }
        );
        addAliasResp
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                $"add alias should succeed: {await addAliasResp.Content.ReadAsStringAsync()}"
            );

        var targetDir = CreateTempDir();
        try
        {
            // 2. Register a local-fs consumer
            var consumerId = await CreateConsumerAsync("ThinE2E Consumer", targetDir);

            // 3. Trigger a manual sync
            var syncResponse = await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);
            syncResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var syncBody = await syncResponse.Content.ReadFromJsonAsync<JsonElement>();
            syncBody.GetProperty("status").GetString().Should().Be("success");
            syncBody.GetProperty("totalEmoji").GetInt32().Should().Be(3);

            // Verify manifest.json exists on disk
            var manifestPath = Path.Combine(targetDir, "manifest.json");
            File.Exists(manifestPath).Should().BeTrue();

            // Verify asset files were written
            File.Exists(Path.Combine(targetDir, "assets", $"{uidAlpha}.png")).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "assets", $"{uidBeta}.png")).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "assets", $"{uidGamma}.png")).Should().BeTrue();

            // 4. Run the downstream component to resolve each alias
            var resultAlpha = await ResolveAlias(targetDir, "alpha");
            resultAlpha.Uid.Should().Be(uidAlpha);
            resultAlpha.PrimaryAlias.Should().Be("alpha");

            var resultBeta = await ResolveAlias(targetDir, "beta");
            resultBeta.Uid.Should().Be(uidBeta);
            resultBeta.PrimaryAlias.Should().Be("beta");

            var resultGamma = await ResolveAlias(targetDir, "gamma");
            resultGamma.Uid.Should().Be(uidGamma);
            resultGamma.PrimaryAlias.Should().Be("gamma");

            // Resolve via secondary alias
            var resultGammaExtra = await ResolveAlias(targetDir, "gamma-extra");
            resultGammaExtra.Uid.Should().Be(uidGamma);
            resultGammaExtra.PrimaryAlias.Should().Be("gamma");

            // ── Pass 2: Remap alias, prove downstream doesn't see it until re-sync ──

            // Give "beta" emoji a secondary alias so we can remap its primary
            var addSecResp = await Client.PostAsJsonAsync(
                $"/api/v1/emoji/{uidBeta}/aliases",
                new { alias = "beta-extra" }
            );
            addSecResp
                .StatusCode.Should()
                .Be(
                    HttpStatusCode.Created,
                    $"add secondary alias should succeed: {await addSecResp.Content.ReadAsStringAsync()}"
                );

            // 5. Remap "beta" alias from uidBeta to uidGamma on the server
            var remapResponse = await Client.PutAsJsonAsync(
                $"/api/v1/aliases/beta/target",
                new { uid = uidGamma }
            );
            remapResponse
                .StatusCode.Should()
                .Be(
                    HttpStatusCode.OK,
                    $"remap should succeed: {await remapResponse.Content.ReadAsStringAsync()}"
                );

            // Give the fire-and-forget on-change sync time to settle (not needed for our test)
            await Task.Delay(200);

            // 6. Run downstream BEFORE re-sync — MUST still resolve "beta" to uidBeta
            //    This is the mechanical proof: if downstream called the live API,
            //    it would see uidGamma. But it reads the local manifest, which hasn't changed.
            var resultBeforeResync = await ResolveAlias(targetDir, "beta");
            resultBeforeResync
                .Uid.Should()
                .Be(uidBeta, "downstream reads stale local manifest — proves no live HTTP calls");

            // 7. Re-sync to update the local manifest
            var reSyncResponse = await Client.PostAsync(
                $"/api/v1/consumers/{consumerId}/sync",
                null
            );
            reSyncResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var reSyncBody = await reSyncResponse.Content.ReadFromJsonAsync<JsonElement>();
            reSyncBody.GetProperty("status").GetString().Should().Be("success");

            // 8. Run downstream AFTER re-sync — now "beta" resolves to uidGamma
            var resultAfterResync = await ResolveAlias(targetDir, "beta");
            resultAfterResync
                .Uid.Should()
                .Be(uidGamma, "after re-sync, downstream picks up the remapped alias");

            // "alpha" still resolves to uidAlpha (was not remapped)
            var resultAlphaAfter = await ResolveAlias(targetDir, "alpha");
            resultAlphaAfter.Uid.Should().Be(uidAlpha);

            // "gamma" still resolves to uidGamma
            var resultGammaAfter = await ResolveAlias(targetDir, "gamma");
            resultGammaAfter.Uid.Should().Be(uidGamma);
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    // ── Consumer sync with subset filter ──

    [Fact]
    public async Task ThinE2E_ConsumerWithSubsetFilter_OnlyReceivesMatchingEmoji()
    {
        await UploadEmojiAsync("filter-alpha", "Alpha", tags: ["reaction"], owner: "team-a");
        await UploadEmojiAsync("filter-beta", "Beta", tags: ["workflow"], owner: "team-b");
        await UploadEmojiAsync(
            "filter-gamma",
            "Gamma",
            tags: ["reaction", "workflow"],
            owner: "team-a"
        );

        var targetDir = CreateTempDir();
        try
        {
            // Register consumer with tag=reaction AND owner=team-a filter
            var consumerResponse = await Client.PostAsJsonAsync(
                "/api/v1/consumers",
                new
                {
                    displayName = "Filtered Consumer",
                    adapter = "local-fs",
                    targetPath = targetDir,
                    triggerMode = "manual",
                    subsetFilter = new { tags = new[] { "reaction" }, owner = "team-a" },
                }
            );
            consumerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            var consumerId = (await consumerResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetString()!;

            // Trigger sync
            var syncResponse = await Client.PostAsync($"/api/v1/consumers/{consumerId}/sync", null);
            syncResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var syncBody = await syncResponse.Content.ReadFromJsonAsync<JsonElement>();
            syncBody.GetProperty("status").GetString().Should().Be("success");

            // Only filter-alpha and filter-gamma should be in the manifest
            var resultAlpha = await ResolveAlias(targetDir, "filter-alpha");
            resultAlpha.Uid.Should().NotBeNullOrEmpty();

            var resultGamma = await ResolveAlias(targetDir, "filter-gamma");
            resultGamma.Uid.Should().NotBeNullOrEmpty();

            // filter-beta should NOT be in the manifest (wrong tags + wrong owner)
            await AssertAliasNotFound(targetDir, "filter-beta");
        }
        finally
        {
            SafeDeleteDir(targetDir);
        }
    }

    // ── Helpers ──

    private async Task<string> UploadEmojiAsync(
        string alias,
        string displayName,
        string[]? tags = null,
        string[]? categories = null,
        string? owner = null
    )
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(PngBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "image/png"
        );
        content.Add(fileContent, "file", "test.png");
        content.Add(new StringContent(alias), "primaryAlias");
        content.Add(new StringContent(displayName), "displayName");

        if (tags is not null)
            foreach (var tag in tags)
                content.Add(new StringContent(tag), "tags");
        if (categories is not null)
            foreach (var cat in categories)
                content.Add(new StringContent(cat), "categories");
        if (owner is not null)
            content.Add(new StringContent(owner), "owner");

        var response = await Client.PostAsync("/api/v1/emoji/upload", content);
        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                $"upload '{alias}' should succeed: {await response.Content.ReadAsStringAsync()}"
            );

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("uid").GetString()!;
    }

    private async Task<string> CreateConsumerAsync(string displayName, string targetPath)
    {
        var payload = new
        {
            displayName,
            adapter = "local-fs",
            targetPath,
            triggerMode = "manual",
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private async Task<ResolveResult> ResolveAlias(string manifestDir, string alias)
    {
        var projectDir = ResolveProjectDir();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"run --project \"{projectDir}\" -- --manifest-dir \"{manifestDir}\" --alias \"{alias}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(0, $"resolve '{alias}' should succeed. stderr: {stderr}");

        var result = JsonSerializer.Deserialize<ResolveResult>(stdout);
        result
            .Should()
            .NotBeNull($"resolve '{alias}' should return valid JSON. stdout: '{stdout}'");
        return result!;
    }

    private async Task AssertAliasNotFound(string manifestDir, string alias)
    {
        var projectDir = ResolveProjectDir();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"run --project \"{projectDir}\" -- --manifest-dir \"{manifestDir}\" --alias \"{alias}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process
            .ExitCode.Should()
            .Be(1, $"resolve '{alias}' should fail (not in manifest). stderr: {stderr}");
    }

    private static string ResolveProjectDir()
    {
        var basePath = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(basePath);

        // Walk up from bin/Debug/net10.0 to the repo root
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EmojiService.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                "Could not find repository root (EmojiService.slnx)"
            );

        return Path.Combine(dir.FullName, "tests", "EmojiService", "EmojiService.Downstream");
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"emoji-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private sealed record ResolveResult(string Uid, string PrimaryAlias, string DisplayName);
}
