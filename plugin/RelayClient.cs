using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GilDom;

public sealed class RelayClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim wake = new(0);
    private readonly object gate = new();
    private readonly List<OutboxItem> outbox = [];
    private int nextToken;
    private string? pendingSecret;
    private bool rotateSecret;
    private Task? loop;

    public int SecretVersion { get; private set; }
    public string SecretStatus { get; private set; } = "";

    public bool IsConnected { get; private set; }
    public string Status { get; private set; } = "Connecting";
    public event Action<JsonElement>? MessageReceived;

    public void Start()
    {
        if (loop is { IsCompleted: false })
            return;
        loop = Task.Run(() => RunAsync(lifetime.Token));
    }

    public void ResetIdentity()
    {
        lock (gate)
        {
            outbox.Clear();
            pendingSecret = null;
            rotateSecret = false;
        }

        IsConnected = false;
        Status = Plugin.Configuration.IsBound ? "Connecting" : "Log in with a character";
    }

    public void QueueSecretChange(string? secret, bool rotate)
    {
        lock (gate)
        {
            pendingSecret = secret;
            rotateSecret = rotate;
        }

        SecretStatus = rotate ? "Asking the server for a new secret." : "Saving the secret.";
        SyncNow();
    }

    public void ClearSecretChange(bool applied)
    {
        lock (gate)
        {
            pendingSecret = null;
            rotateSecret = false;
        }

        if (applied)
        {
            SecretVersion++;
            SecretStatus = "The server saved the new secret. Your friend code is unchanged.";
        }
    }

    public void SyncNow()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A sync is already queued.
        }
    }

    public void Send(object message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            lock (gate)
                outbox.Add(new OutboxItem { Token = ++nextToken, Json = json });
            SyncNow();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        SyncNow();
        http.Dispose();
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        var idle = TimeSpan.FromSeconds(5);
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await WaitForWork(idle, cancel).ConfigureAwait(false);
                idle = TimeSpan.FromSeconds(5);
                if (HasOutbox())
                    await Task.Delay(100, cancel).ConfigureAwait(false);

                var built = await OnFramework(BuildBody).ConfigureAwait(false);
                if (built.ContentId.Length == 0)
                {
                    IsConnected = false;
                    Status = "Log in with a character";
                    continue;
                }

                var response = await PostAsync(built.Body, cancel).ConfigureAwait(false);
                lock (gate)
                    outbox.RemoveAll(item => built.Tokens.Contains(item.Token));

                var captured = response;
                var contentId = built.ContentId;
                await OnFramework(() =>
                {
                    if (Plugin.Configuration.ActiveContentId != contentId)
                        return true;

                    IsConnected = true;
                    Status = "Connected · " + DateTime.Now.ToString("t");
                    MessageReceived?.Invoke(captured);
                    return true;
                }).ConfigureAwait(false);

                if (HasOutbox())
                    SyncNow();
            }
            catch (Exception ex) when (!cancel.IsCancellationRequested)
            {
                IsConnected = false;
                Status = ex.Message;
                var dropSecretChange = false;
                lock (gate)
                {
                    dropSecretChange = rotateSecret || pendingSecret != null;
                    pendingSecret = null;
                    rotateSecret = false;
                }

                if (dropSecretChange)
                    SecretStatus = ex.Message;
                Plugin.Log.Warning(ex, "GilDom sync failed");
                idle = TimeSpan.FromSeconds(3);
            }
        }
    }

    private (string Body, int[] Tokens, string ContentId) BuildBody()
    {
        var contentId = Plugin.Configuration.ActiveContentId;
        if (contentId.Length == 0)
            return ("", [], "");

        List<OutboxItem> batch;
        lock (gate)
            batch = outbox.ToList();

        var draft = new Draft();
        foreach (var item in batch)
            Fold(item.Json, draft);

        var want = new JsonArray();
        foreach (var friend in Plugin.Configuration.Friends)
        {
            var code = friend.Code.ToUpperInvariant();
            if (Session.IsFriendCode(code))
                want.Add(code);
        }

        var updates = new JsonArray();
        foreach (var update in draft.Updates.Values)
            updates.Add(update);
        var actions = new JsonArray();
        foreach (var action in draft.Actions.Values)
            actions.Add(action);

        string? nextSecret;
        var rotate = false;
        lock (gate)
        {
            nextSecret = pendingSecret;
            rotate = rotateSecret;
        }

        var payload = new JsonObject
        {
            ["want"] = want,
            ["updates"] = updates,
            ["actions"] = actions,
        };
        if (Plugin.Configuration.LinkSecret.Length >= 16)
            payload["secret"] = Plugin.Configuration.LinkSecret;
        if (rotate)
            payload["rotateSecret"] = true;
        else if (!string.IsNullOrEmpty(nextSecret))
            payload["newSecret"] = nextSecret;
        return (payload.ToJsonString(), batch.Select(item => item.Token).ToArray(), contentId);
    }

    private static void Fold(string json, Draft draft)
    {
        JsonElement message;
        try
        {
            using var doc = JsonDocument.Parse(json);
            message = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        var type = Text(message, "t");
        switch (type)
        {
            case "friend_request":
            case "role_proposal":
                AddAction(draft, type, Text(message, "to"), 0, Text(message, "role"));
                break;
            case "friend_accept":
            case "friend_decline":
            case "unlink":
            case "role_accept":
            case "role_decline":
            case "role_cancel":
                AddAction(draft, type, Text(message, "to"), 0, "");
                break;
            case "state":
                FoldState(draft, message);
                break;
        }
    }

    private static void FoldState(Draft draft, JsonElement message)
    {
        var to = Text(message, "to").ToUpperInvariant();
        var kind = Text(message, "kind");
        if (!message.TryGetProperty("body", out var body))
            return;

        if (kind is "tribute" or "tribute_paid")
        {
            AddAction(draft, kind, to, Long(body, "amount"), "", kind == "tribute" ? Text(body, "name") : "", kind == "tribute" ? Text(body, "world") : "");
            return;
        }

        if (kind is not ("role" or "dynamics" or "gil") || body.ValueKind != JsonValueKind.Object)
            return;

        draft.Updates[to + "\n" + kind] = new JsonObject
        {
            ["to"] = to,
            ["kind"] = kind,
            ["body"] = JsonNode.Parse(body.GetRawText()),
        };
    }

    private static void AddAction(Draft draft, string type, string to, long amount, string role, string name = "", string world = "")
    {
        to = to.ToUpperInvariant();
        var action = new JsonObject
        {
            ["t"] = type,
            ["to"] = to,
        };
        if (type is "tribute" or "tribute_paid")
            action["amount"] = amount;
        if (type == "tribute" && name.Length > 0 && world.Length > 0)
        {
            action["name"] = name;
            action["world"] = world;
        }

        if (role is "Dom" or "Sub")
            action["role"] = role;
        draft.Actions[type + "\n" + to] = action;
    }

    private async Task<JsonElement> PostAsync(string body, CancellationToken cancel)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(ServerAddress.Url, content, cancel).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorMessage(text, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string ErrorMessage(string text, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                var value = message.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch (JsonException)
        {
            // The server returned a non-JSON error page.
        }

        return string.IsNullOrWhiteSpace(text) ? "GilDom returned " + status : text;
    }

    private bool HasOutbox()
    {
        lock (gate)
            return outbox.Count > 0;
    }

    private async Task WaitForWork(TimeSpan idle, CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var delay = Task.Delay(idle, linked.Token);
        var signal = wake.WaitAsync(linked.Token);
        await Task.WhenAny(delay, signal).ConfigureAwait(false);
        linked.Cancel();
        try
        {
            await Task.WhenAll(delay, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Whichever side lost the race was cancelled.
        }
    }

    private static Task<T> OnFramework<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Plugin.Framework.RunOnTick(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : 0;
    }

    private sealed class OutboxItem
    {
        public int Token { get; set; }
        public string Json { get; set; } = "";
    }

    private sealed class Draft
    {
        public Dictionary<string, JsonObject> Updates { get; } = [];
        public Dictionary<string, JsonObject> Actions { get; } = [];
    }
}
