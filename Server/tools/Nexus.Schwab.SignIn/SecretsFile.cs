using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Schwab.SignIn;

/// <summary>Sets keys in a user secrets file, keeping every other key.</summary>
internal static class SecretsFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Save(string path, IReadOnlyDictionary<string, string> values)
    {
        var secrets = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
              ?? throw new InvalidDataException($"{path} does not hold a JSON object.")
            : new JsonObject();

        foreach (var (key, value) in values)
        {
            secrets[key] = value;
        }

        var bytes = Encoding.UTF8.GetBytes(secrets.ToJsonString(Indented));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // The file is replaced in one move, so a failure leaves the old secrets whole.
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
