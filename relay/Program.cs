using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
// Local play stays on localhost. Fly sets ASPNETCORE_URLS to the port its proxy dials.
var listen = builder.Configuration["urls"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? "http://127.0.0.1:8787";
builder.WebHost.UseUrls(listen);
var app = builder.Build();
var dataFile = builder.Configuration["GILDOM_DATA"];
if (string.IsNullOrWhiteSpace(dataFile))
    dataFile = Path.Combine(app.Environment.ContentRootPath, "relay-data.json");
var hub = new RelayHub(dataFile);
hub.RewriteStore();

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapPost("/sync", async (HttpRequest request) =>
{
    var text = await ReadBodyAsync(request);
    if (text == null)
        return Results.Json(new { message = "Message was too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);
    try
    {
        using var doc = JsonDocument.Parse(text);
        return hub.Sync(doc.RootElement);
    }
    catch (JsonException)
    {
        return Results.Json(new { message = "That message was not JSON." }, statusCode: StatusCodes.Status400BadRequest);
    }
});

app.Run();

static async Task<string?> ReadBodyAsync(HttpRequest request)
{
    const int limit = 65536;
    using var reader = new StreamReader(request.Body);
    var buffer = new char[4096];
    var builder = new StringBuilder();
    while (true)
    {
        var read = await reader.ReadAsync(buffer);
        if (read == 0)
            return builder.ToString();
        if (builder.Length + read > limit)
            return null;
        builder.Append(buffer, 0, read);
    }
}

internal sealed class RelayHub(string storePath)
{
    private static readonly Regex CodePattern = new("^[0-9A-Z]{8}$", RegexOptions.Compiled);
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(45);
    private readonly object gate = new();
    private readonly Dictionary<string, Record> records = Load(storePath);
    private readonly Dictionary<string, Record> bySecret = new(StringComparer.Ordinal);
    private bool secretsIndexed;

    public void RewriteStore()
    {
        lock (gate)
            Save();
    }

    public IResult Sync(JsonElement root)
    {
        var secret = Text(root, "secret");
        string? issued = null;
        if (secret.Length == 0)
        {
            issued = MintSecret();
            secret = issued;
        }
        else if (!IsSecret(secret))
        {
            return Results.Json(new { message = "Secret is not valid." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var hash = HashSecret(secret);
        lock (gate)
        {
            IndexSecrets();
            var dirty = false;
            if (!bySecret.TryGetValue(hash, out var self))
            {
                self = new Record { Code = MintCode(), SecretHash = hash };
                records[self.Code] = self;
                bySecret[hash] = self;
                dirty = true;
            }

            var replacement = issued == null ? ChooseReplacement(root, secret) : null;
            if (replacement != null)
            {
                if (!IsSecret(replacement))
                    return Results.Json(new { message = "Secret is not valid." }, statusCode: StatusCodes.Status400BadRequest);

                var nextHash = HashSecret(replacement);
                if (bySecret.TryGetValue(nextHash, out var owner) && !ReferenceEquals(owner, self))
                    return Results.Json(new { message = "That secret is already in use." }, statusCode: StatusCodes.Status400BadRequest);

                bySecret.Remove(hash);
                self.SecretHash = nextHash;
                bySecret[nextHash] = self;
                dirty = true;
            }

            dirty |= ApplyActions(self, root);
            dirty |= ApplyUpdates(self, root);
            self.LastSeenUtc = DateTime.UtcNow;
            if (dirty)
                Save();

            var reply = BuildReply(self, root);
            if (issued != null)
                reply["secret"] = issued;
            else if (replacement != null)
                reply["secret"] = replacement;
            return Results.Json(reply);
        }
    }

    private void IndexSecrets()
    {
        if (secretsIndexed)
            return;

        foreach (var record in records.Values)
        {
            if (!string.IsNullOrEmpty(record.SecretHash))
                bySecret[record.SecretHash] = record;
        }

        secretsIndexed = true;
    }

    private string MintCode() => MintToken("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 8, code => !records.ContainsKey(code));

    private static string MintSecret() => MintToken("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 64, _ => true);

    private static string? ChooseReplacement(JsonElement root, string current)
    {
        if (root.TryGetProperty("rotateSecret", out var rotate) && rotate.ValueKind == JsonValueKind.True)
            return MintSecret();

        var chosen = Text(root, "newSecret");
        if (chosen.Length == 0 || chosen == current)
            return null;
        return chosen;
    }

    private static string MintToken(string alphabet, int length, Func<string, bool> accept)
    {
        Span<char> chars = stackalloc char[length];
        for (var attempt = 0; attempt < 32; attempt++)
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            var token = new string(chars);
            if (accept(token))
                return token;
        }

        throw new InvalidOperationException("Could not mint a token.");
    }

    private static bool IsSecret(string secret)
    {
        if (secret.Length is < 16 or > 64)
            return false;
        foreach (var character in secret)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')))
                return false;
        }

        return true;
    }

    private bool ApplyActions(Record self, JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return false;

        var dirty = false;
        foreach (var action in actions.EnumerateArray())
        {
            var to = Text(action, "to").ToUpperInvariant();
            if (!CodePattern.IsMatch(to) || to == self.Code)
                continue;

            switch (Text(action, "t"))
            {
                case "friend_request":
                    dirty |= FriendRequest(self, to, RoleOf(action));
                    break;
                case "friend_accept":
                    dirty |= FriendAccept(self, to);
                    break;
                case "friend_decline":
                    dirty |= FriendDecline(self, to);
                    break;
                case "unlink":
                    dirty |= Unlink(self, to);
                    break;
                case "role_proposal":
                    dirty |= ProposeRole(self, to, RoleOf(action));
                    break;
                case "role_accept":
                    dirty |= AcceptRole(self, to);
                    break;
                case "role_decline":
                    dirty |= ClearProposal(self, to, own: false);
                    break;
                case "role_cancel":
                    dirty |= ClearProposal(self, to, own: true);
                    break;
                case "tribute":
                    dirty |= BumpNotice(self, to, paid: false, Long(action, "amount"), Text(action, "name"), Text(action, "world"));
                    break;
                case "tribute_paid":
                    dirty |= BumpNotice(self, to, paid: true, Long(action, "amount"), "", "");
                    break;
            }
        }

        return dirty;
    }

    private bool ApplyUpdates(Record self, JsonElement root)
    {
        if (!root.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array)
            return false;

        var dirty = false;
        foreach (var update in updates.EnumerateArray())
        {
            var to = Text(update, "to").ToUpperInvariant();
            if (!CodePattern.IsMatch(to) || !self.Links.Contains(to))
                continue;

            var edge = EdgeFor(self, to);
            var body = update.TryGetProperty("body", out var found) ? found : default;
            switch (Text(update, "kind"))
            {
                case "role":
                    var role = Text(body, "role");
                    if (role is not ("Sub" or "Dom" or "Unset"))
                        role = "Unset";
                    var confirmed = body.ValueKind == JsonValueKind.Object
                        && body.TryGetProperty("confirmed", out var flag)
                        && flag.ValueKind == JsonValueKind.True;
                    if (!confirmed && edge.Confirmed && edge.Role is "Dom" or "Sub" && edge.Role == role)
                        break;
                    if (edge.Role != role || edge.Confirmed != confirmed)
                    {
                        edge.Role = role;
                        edge.Confirmed = confirmed;
                        dirty = true;
                    }

                    break;
                case "dynamics":
                    if (body.ValueKind != JsonValueKind.Object)
                        break;
                    var dynamics = body.GetRawText();
                    if (edge.Dynamics != dynamics)
                    {
                        edge.Dynamics = dynamics;
                        dirty = true;
                    }

                    break;
                case "gil":
                    if (body.ValueKind != JsonValueKind.Object)
                        break;
                    var real = Long(body, "real");
                    var pocket = Long(body, "pocket");
                    var owed = Long(body, "owed");
                    var taxGil = Long(body, "tax");
                    if (!edge.HasGil || edge.Real != real || edge.Pocket != pocket || edge.Owed != owed || edge.TaxGil != taxGil)
                    {
                        edge.HasGil = true;
                        edge.Real = real;
                        edge.Pocket = pocket;
                        edge.Owed = owed;
                        edge.TaxGil = taxGil;
                        dirty = true;
                    }

                    break;
            }
        }

        return dirty;
    }

    private bool FriendRequest(Record self, string to, string role)
    {
        if (role is not ("Dom" or "Sub"))
            return false;

        var target = FindOrPlaceholder(to);
        var existing = target.Pending.FindIndex(item => item.Code == self.Code);
        if (existing >= 0 && target.Pending[existing].Role == role)
            return false;

        if (existing >= 0)
            target.Pending.RemoveAt(existing);
        target.Pending.Add((self.Code, role));
        return true;
    }

    private bool FriendAccept(Record self, string to)
    {
        if (!records.TryGetValue(to, out var other))
            return false;

        var offer = self.Pending.FirstOrDefault(item => item.Code == to).Role;
        var dirty = self.Links.Add(to);
        dirty |= other.Links.Add(self.Code);
        dirty |= self.Pending.RemoveAll(item => item.Code == to) > 0;
        dirty |= other.Pending.RemoveAll(item => item.Code == self.Code) > 0;
        if (offer is "Dom" or "Sub")
        {
            var mine = offer == "Dom" ? "Sub" : "Dom";
            var theirs = EdgeFor(other, self.Code);
            var ours = EdgeFor(self, to);
            if (theirs.Role != offer || !theirs.Confirmed || ours.Role != mine || !ours.Confirmed)
                dirty = true;
            theirs.Role = offer;
            theirs.Confirmed = true;
            theirs.ProposedRole = "";
            ours.Role = mine;
            ours.Confirmed = true;
            ours.ProposedRole = "";
        }

        return dirty;
    }

    private bool FriendDecline(Record self, string to)
    {
        var removed = self.Pending.RemoveAll(item => item.Code == to) > 0;
        if (records.TryGetValue(to, out var other))
            removed |= other.Pending.RemoveAll(item => item.Code == self.Code) > 0;
        return removed;
    }

    private bool Unlink(Record self, string to)
    {
        var dirty = self.Links.Remove(to);
        self.Edges.Remove(to);
        if (!records.TryGetValue(to, out var other))
            return dirty;

        dirty |= other.Links.Remove(self.Code);
        dirty |= other.Edges.Remove(self.Code);
        return dirty;
    }

    private bool ProposeRole(Record self, string to, string role)
    {
        if (role is not ("Dom" or "Sub") || !self.Links.Contains(to))
            return false;

        var edge = EdgeFor(self, to);
        if (edge.ProposedRole == role)
            return false;
        edge.ProposedRole = role;
        return true;
    }

    private bool AcceptRole(Record self, string to)
    {
        if (!self.Links.Contains(to) || !records.TryGetValue(to, out var other))
            return false;
        if (!other.Edges.TryGetValue(self.Code, out var theirs))
            return false;
        if (theirs.ProposedRole is not ("Dom" or "Sub"))
            return false;

        var offer = theirs.ProposedRole;
        var mine = offer == "Dom" ? "Sub" : "Dom";
        var ours = EdgeFor(self, to);
        theirs.Role = offer;
        theirs.Confirmed = true;
        theirs.ProposedRole = "";
        ours.Role = mine;
        ours.Confirmed = true;
        ours.ProposedRole = "";
        return true;
    }

    private bool ClearProposal(Record self, string to, bool own)
    {
        if (own)
        {
            if (!self.Edges.TryGetValue(to, out var edge) || string.IsNullOrEmpty(edge.ProposedRole))
                return false;
            edge.ProposedRole = "";
            return true;
        }

        if (!records.TryGetValue(to, out var other) || !other.Edges.TryGetValue(self.Code, out var theirs))
            return false;
        if (string.IsNullOrEmpty(theirs.ProposedRole))
            return false;
        theirs.ProposedRole = "";
        return true;
    }

    private static string RoleOf(JsonElement action)
    {
        var role = Text(action, "role");
        return role is "Dom" or "Sub" ? role : "";
    }

    private bool BumpNotice(Record self, string to, bool paid, long amount, string name, string world)
    {
        if (!self.Links.Contains(to))
            return false;

        var edge = EdgeFor(self, to);
        if (paid)
        {
            edge.PaidSeq++;
            edge.PaidAmount = amount;
        }
        else
        {
            edge.TributeSeq++;
            edge.TributeAmount = amount;
            edge.TradeName = ShortLabel(name, 32);
            edge.TradeWorld = ShortLabel(world, 32);
        }

        return true;
    }

    private static string ShortLabel(string value, int max)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > max)
            return "";

        foreach (var character in trimmed)
        {
            if (character is '\'' or '-' or ' ' || char.IsLetter(character))
                continue;
            return "";
        }

        return trimmed;
    }

    private JsonObject BuildReply(Record self, JsonElement root)
    {
        var requests = new JsonArray();
        foreach (var pending in self.Pending)
        {
            requests.Add(new JsonObject
            {
                ["from"] = pending.Code,
                ["role"] = pending.Role,
            });
        }

        var friends = new JsonArray();
        var closed = new JsonArray();
        if (root.TryGetProperty("want", out var want) && want.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in want.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var code = (item.GetString() ?? "").ToUpperInvariant();
                if (!CodePattern.IsMatch(code) || code == self.Code)
                    continue;
                if (self.Links.Contains(code) && records.TryGetValue(code, out var other))
                    friends.Add(FriendRow(self, other));
                else if (!PendingEitherWay(self, code))
                    closed.Add(code);
            }
        }

        return new JsonObject
        {
            ["code"] = self.Code,
            ["requests"] = requests,
            ["friends"] = friends,
            ["closed"] = closed,
        };
    }

    private JsonObject FriendRow(Record self, Record other)
    {
        other.Edges.TryGetValue(self.Code, out var edge);
        self.Edges.TryGetValue(other.Code, out var mine);
        var row = new JsonObject
        {
            ["code"] = other.Code,
            ["online"] = other.LastSeenUtc != DateTime.MinValue && DateTime.UtcNow - other.LastSeenUtc <= OnlineWindow,
            ["role"] = edge?.Role ?? "Unset",
            ["confirmed"] = edge?.Confirmed ?? false,
            ["myRole"] = mine?.Role ?? "Unset",
            ["myConfirmed"] = mine?.Confirmed ?? false,
            ["myProposal"] = mine?.ProposedRole ?? "",
            ["theirProposal"] = edge?.ProposedRole ?? "",
            ["tributeSeq"] = edge?.TributeSeq ?? 0,
            ["tributeAmount"] = edge?.TributeAmount ?? 0,
            ["paidSeq"] = edge?.PaidSeq ?? 0,
            ["paidAmount"] = edge?.PaidAmount ?? 0,
        };
        if (!string.IsNullOrEmpty(edge?.TradeName))
        {
            row["tradeName"] = edge.TradeName;
            row["tradeWorld"] = edge.TradeWorld;
        }

        if (!string.IsNullOrEmpty(edge?.Dynamics))
        {
            try
            {
                row["dynamics"] = JsonNode.Parse(edge.Dynamics);
            }
            catch (JsonException)
            {
                // Leave the broken blob out of the reply.
            }
        }

        if (edge is { HasGil: true })
        {
            row["gil"] = new JsonObject
            {
                ["real"] = edge.Real,
                ["pocket"] = edge.Pocket,
                ["owed"] = edge.Owed,
                ["tax"] = edge.TaxGil,
            };
        }

        return row;
    }

    private bool PendingEitherWay(Record self, string code)
    {
        if (self.Pending.Any(item => item.Code == code))
            return true;
        return records.TryGetValue(code, out var other) && other.Pending.Any(item => item.Code == self.Code);
    }

    private Record FindOrPlaceholder(string code)
    {
        if (records.TryGetValue(code, out var found))
            return found;

        found = new Record { Code = code };
        records[code] = found;
        return found;
    }

    private static Edge EdgeFor(Record self, string to)
    {
        if (!self.Edges.TryGetValue(to, out var edge))
        {
            edge = new Edge();
            self.Edges[to] = edge;
        }

        return edge;
    }

    private void Save()
    {
        var snapshot = records.Values.Select(record => new StoredRecord
        {
            Code = record.Code,
            SecretHash = record.SecretHash,
            Links = record.Links.ToList(),
            Pending = record.Pending.Select(item => new StoredPending { Code = item.Code, Role = item.Role }).ToList(),
            Edges = record.Edges.Select(pair => new StoredEdge
            {
                To = pair.Key,
                Role = pair.Value.Role,
                Confirmed = pair.Value.Confirmed,
                ProposedRole = pair.Value.ProposedRole,
                Dynamics = pair.Value.Dynamics,
                HasGil = pair.Value.HasGil,
                Real = pair.Value.Real,
                Pocket = pair.Value.Pocket,
                Owed = pair.Value.Owed,
                TaxGil = pair.Value.TaxGil,
                TributeSeq = pair.Value.TributeSeq,
                TributeAmount = pair.Value.TributeAmount,
                PaidSeq = pair.Value.PaidSeq,
                PaidAmount = pair.Value.PaidAmount,
            }).ToList(),
        }).ToList();
        File.WriteAllText(storePath, JsonSerializer.Serialize(snapshot));
    }

    private static Dictionary<string, Record> Load(string path)
    {
        var result = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return result;

        var stored = JsonSerializer.Deserialize<List<StoredRecord>>(File.ReadAllText(path)) ?? [];
        foreach (var item in stored)
        {
            if (string.IsNullOrWhiteSpace(item.Code))
                continue;

            var hash = item.SecretHash;
            if (string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(item.Secret))
                hash = HashSecret(item.Secret);

            var record = new Record
            {
                Code = item.Code.ToUpperInvariant(),
                SecretHash = hash,
                Links = item.Links.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Pending = item.Pending.Select(pending => (pending.Code.ToUpperInvariant(), pending.Role is "Dom" or "Sub" ? pending.Role : "")).ToList(),
            };
            foreach (var edge in item.Edges)
            {
                if (string.IsNullOrWhiteSpace(edge.To))
                    continue;
                record.Edges[edge.To.ToUpperInvariant()] = new Edge
                {
                    Role = edge.Role,
                    Confirmed = edge.Confirmed,
                    ProposedRole = edge.ProposedRole ?? "",
                    Dynamics = edge.Dynamics,
                    HasGil = edge.HasGil,
                    Real = edge.Real,
                    Pocket = edge.Pocket,
                    Owed = edge.Owed,
                    TaxGil = edge.TaxGil,
                    TributeSeq = edge.TributeSeq,
                    TributeAmount = edge.TributeAmount,
                    PaidSeq = edge.PaidSeq,
                    PaidAmount = edge.PaidAmount,
                };
            }

            result[record.Code] = record;
        }

        return result;
    }

    private static string HashSecret(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes);
    }

    private static bool SameSecret(string left, string right)
    {
        if (left.Length != right.Length)
            return false;
        var mismatch = 0;
        for (var i = 0; i < left.Length; i++)
            mismatch |= left[i] ^ right[i];
        return mismatch == 0;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long Long(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return 0;
        if (value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : 0;
    }

    private sealed class Record
    {
        public string Code { get; set; } = "";
        public string SecretHash { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
        public HashSet<string> Links { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Code, string Role)> Pending { get; set; } = [];
        public Dictionary<string, Edge> Edges { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Edge
    {
        public string Role { get; set; } = "Unset";
        public bool Confirmed { get; set; }
        public string ProposedRole { get; set; } = "";
        public string? Dynamics { get; set; }
        public bool HasGil { get; set; }
        public long Real { get; set; }
        public long Pocket { get; set; }
        public long Owed { get; set; }
        public long TaxGil { get; set; }
        public int TributeSeq { get; set; }
        public long TributeAmount { get; set; }
        public int PaidSeq { get; set; }
        public long PaidAmount { get; set; }
        public string TradeName { get; set; } = "";
        public string TradeWorld { get; set; } = "";
    }

    private sealed class StoredRecord
    {
        public string Code { get; set; } = "";
        public string Secret { get; set; } = "";
        public string SecretHash { get; set; } = "";
        public List<string> Links { get; set; } = [];
        public List<StoredPending> Pending { get; set; } = [];
        public List<StoredEdge> Edges { get; set; } = [];
    }

    private sealed class StoredPending
    {
        public string Code { get; set; } = "";
        public string Role { get; set; } = "";
    }

    private sealed class StoredEdge
    {
        public string To { get; set; } = "";
        public string Role { get; set; } = "Unset";
        public bool Confirmed { get; set; }
        public string? ProposedRole { get; set; }
        public string? Dynamics { get; set; }
        public bool HasGil { get; set; }
        public long Real { get; set; }
        public long Pocket { get; set; }
        public long Owed { get; set; }
        public long TaxGil { get; set; }
        public int TributeSeq { get; set; }
        public long TributeAmount { get; set; }
        public int PaidSeq { get; set; }
        public long PaidAmount { get; set; }
    }
}
