using FoxTrans.Desktop.Services;
using Xunit;

public sealed class DesktopSecretRedactorTests
{
    [Fact]
    public void RedactsLiteralKeysPromptsHeadersAudioAndEndpointSecrets()
    {
        const string key = "literal-super-secret-key";
        ResolvedExecutionPlan plan = DesktopTestPlans.Create(
            PipelineKind.DirectAudioTranslation,
            apiKey: key);
        var redactor = new DesktopSecretRedactor(plan);
        string audio = Convert.ToBase64String(new byte[256]);
        string message =
            $"Authorization: Bearer {key} full direct prompt " +
            $"https://user:pass@example.test/path?api_key={key} {audio}";

        string safe = redactor.Redact(message);

        Assert.DoesNotContain(key, safe, StringComparison.Ordinal);
        Assert.DoesNotContain("full direct prompt", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user:pass", safe, StringComparison.Ordinal);
        Assert.DoesNotContain(audio, safe, StringComparison.Ordinal);
        Assert.Contains("<redacted>", safe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeNeverStoresSecretsInNodeErrorsOrNotifications()
    {
        const string key = "environment-secret-value";
        ResolvedExecutionPlan plan = DesktopTestPlans.Create(
            PipelineKind.BatchTranscriptionTranslation,
            apiKey: key);
        PipelineViewDefinition definition = PipelineTopologyBuilder.Build(plan);
        await using var bridge = new DesktopEventBridge(definition, plan);

        bridge.Report(AppEvent.ApiError(
            $"Authorization: Bearer {key}; endpoint https://u:p@example.test/v1?token={key}"));
        await WaitUntilAsync(() => bridge.Snapshot.Notification is not null);
        string snapshot = bridge.Snapshot.ToString()!;

        Assert.DoesNotContain(key, snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("u:p", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization: Bearer", snapshot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TopologyShowsOnlyCredentialAndPromptConfiguredState()
    {
        const string key = "never-render-this-api-key";
        PipelineViewDefinition topology = PipelineTopologyBuilder.Build(
            DesktopTestPlans.Create(
                PipelineKind.BatchTranscriptionTranslation,
                apiKey: key));
        string rendered = string.Join(
            "\n",
            topology.Nodes.SelectMany(node =>
                node.Settings.Select(setting =>
                    $"{node.Title} {setting.Name} {setting.Value}")));

        Assert.DoesNotContain(key, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("full translation prompt", rendered, StringComparison.Ordinal);
        Assert.Contains("Credential configured", rendered, StringComparison.Ordinal);
        Assert.Contains("Prompt", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidUnresolvedConfigurationStillRedactsConfiguredSecrets()
    {
        const string key = "invalid-config-literal-key";
        const string prompt = "private invalid configuration prompt";
        var config = new FoxTransConfig(
            Pipeline: new(
                Speech: new OpenAiChatAudioConfig(
                    "not an endpoint",
                    key,
                    "model",
                    prompt)));
        var redactor = new DesktopSecretRedactor(null, config);

        string safe = redactor.Redact(
            $"Could not validate {key}; prompt was {prompt}.");

        Assert.DoesNotContain(key, safe, StringComparison.Ordinal);
        Assert.DoesNotContain(prompt, safe, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
