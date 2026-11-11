using System.Text;
using System.Text.Json;

namespace GilDom;

public readonly record struct PastedMoodle(string Title, string Description, int IconId, int StatusType);

public static class MoodlePaste
{
    public const int DefaultIconId = 215125;

    public static bool TryRead(string? clipboard, out PastedMoodle moodle, out string error)
    {
        moodle = default;
        error = "Copy a moodle in Moodles first. Use the copy button on the moodle list.";
        if (string.IsNullOrWhiteSpace(clipboard))
            return false;

        var text = clipboard.Trim();
        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            try
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();
            }
            catch (FormatException)
            {
                return false;
            }
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return false;
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (Has(root, "Statuses") && Has(root, "ApplicationType") && !Has(root, "Title") && !Has(root, "IconID"))
            {
                error = "That clipboard text is a Moodles preset. Copy one moodle, not a preset.";
                return false;
            }

            var title = String(root, "Title");
            if (string.IsNullOrWhiteSpace(title))
                return false;

            var icon = Int(root, "IconID");
            if (icon is > 0 and < 200000)
                icon += 200000;
            if (icon < 200000)
                icon = DefaultIconId;

            moodle = new PastedMoodle(title, String(root, "Description"), icon, Status(root));
            error = "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Has(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static string String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static int Status(JsonElement root)
    {
        if (!root.TryGetProperty("Type", out var value))
            return 1;
        if (value.TryGetInt32(out var number))
            return Math.Clamp(number, 0, 2);
        if (value.ValueKind != JsonValueKind.String)
            return 1;

        return value.GetString() switch
        {
            "Positive" => 0,
            "Special" => 2,
            _ => 1,
        };
    }
}
