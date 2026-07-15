using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IRCClient;

// Encrypts a string property on the way to/from JSON using Windows DPAPI (tied to
// the current Windows user account), so saved IRC passwords aren't stored in
// plaintext in connections.json. The in-memory value is always the real password.
public class ProtectedStringConverter : JsonConverter<string>
{
    private static readonly byte[] Entropy = "IRCClient.SavedConnection.Password"u8.ToArray();

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var stored = reader.GetString();
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            var cipher = Convert.FromBase64String(stored);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Not decryptable (different Windows user/machine, corrupted value, or
            // plaintext left over from before this converter existed) — fail safe
            // to empty rather than throwing and losing the whole connection library.
            return "";
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue("");
            return;
        }
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        writer.WriteStringValue(Convert.ToBase64String(cipher));
    }
}
