using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewAgentBridge.Serialization
{
    /// <summary>
    /// Shared System.Text.Json settings. .NET 6 (the game's runtime) has no JsonNamingPolicy.SnakeCaseLower
    /// (added in .NET 8), so this uses a local snake_case policy.
    /// </summary>
    public static class StateJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false
        };

        public static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };

        public static string Serialize<T>(T value, bool indented = false)
        {
            return JsonSerializer.Serialize(value, indented ? IndentedOptions : Options);
        }

        /// <summary>Flatten a JSON document into "path" -> scalar text, e.g. "inventory.slots[3].item.stack" -> "5".</summary>
        public static Dictionary<string, string> Flatten(string json)
        {
            var result = new Dictionary<string, string>();
            using JsonDocument doc = JsonDocument.Parse(json);
            FlattenInto(doc.RootElement, "", result);
            return result;
        }

        private static void FlattenInto(JsonElement element, string path, Dictionary<string, string> result)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                        FlattenInto(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", result);
                    break;

                case JsonValueKind.Array:
                    int i = 0;
                    foreach (JsonElement child in element.EnumerateArray())
                        FlattenInto(child, $"{path}[{i++}]", result);
                    if (i == 0)
                        result[path] = "[]";
                    break;

                default:
                    result[path] = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
                    break;
            }
        }
    }

    /// <summary>PascalCase -> snake_case, e.g. "QualifiedItemId" -> "qualified_item_id", "TileX" -> "tile_x".</summary>
    public sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var builder = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    bool prevLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    bool acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (prevLowerOrDigit || acronymEnd)
                        builder.Append('_');
                    builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }
    }
}
