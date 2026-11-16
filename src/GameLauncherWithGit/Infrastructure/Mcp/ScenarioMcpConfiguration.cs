using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Mcp;

/// <summary>Local listener configuration. Secrets are never included in diagnostics.</summary>
public sealed class ScenarioMcpConfiguration
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 47831;
    public string StructureKey { get; set; } = "";
    public string FullKey { get; set; } = "";

    public static ScenarioMcpConfiguration Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var result = JsonSerializer.Deserialize<ScenarioMcpConfiguration>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
            if (result is null) throw new InvalidOperationException();
            result.Validate();
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException("Scenario MCP configuration could not be loaded.");
        }
    }

    public void Validate()
    {
        if (!Enabled) return;
        if (Port < 1 || Port > 65535 || !IsKey(StructureKey) || !IsKey(FullKey) || StructureKey == FullKey)
            throw new InvalidOperationException("Scenario MCP requires a valid port and two distinct 256-bit hexadecimal keys.");
    }

    internal ScenarioMaterialAccessLevel? Authenticate(string? authorization)
    {
        if (!Enabled || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var supplied = authorization[7..];
        if (!IsKey(supplied)) return null;
        var bytes = Encoding.ASCII.GetBytes(supplied);
        if (CryptographicOperations.FixedTimeEquals(bytes, Encoding.ASCII.GetBytes(FullKey))) return ScenarioMaterialAccessLevel.Full;
        if (CryptographicOperations.FixedTimeEquals(bytes, Encoding.ASCII.GetBytes(StructureKey))) return ScenarioMaterialAccessLevel.Structure;
        return null;
    }

    private static bool IsKey(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
