using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeBoxX.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public bool AutoSave { get; set; }
    public double FontSize { get; set; } = 14;
    public string? ActiveExtensionThemeId { get; set; }
    public string? EncryptedGeminiApiKey { get; set; }
    [JsonIgnore] public bool HasGeminiApiKey => !string.IsNullOrWhiteSpace(EncryptedGeminiApiKey);
    public List<string> RecentFiles { get; set; } = [];
    public List<string> RecentProjects { get; set; } = [];

    private static string SettingsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBoxX");
    private static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch
        {
            // A damaged preferences file must never block opening the editor.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void SetGeminiApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Enter a Gemini API key.", nameof(apiKey));
        var clearBytes = Encoding.UTF8.GetBytes(apiKey.Trim());
        var protectedBytes = ProtectedData.Protect(clearBytes, GetEntropy(), DataProtectionScope.CurrentUser);
        EncryptedGeminiApiKey = Convert.ToBase64String(protectedBytes);
        CryptographicOperations.ZeroMemory(clearBytes);
    }

    public string? GetGeminiApiKey()
    {
        if (string.IsNullOrWhiteSpace(EncryptedGeminiApiKey)) return null;
        try
        {
            var protectedBytes = Convert.FromBase64String(EncryptedGeminiApiKey);
            var clearBytes = ProtectedData.Unprotect(protectedBytes, GetEntropy(), DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clearBytes); }
            finally { CryptographicOperations.ZeroMemory(clearBytes); }
        }
        catch
        {
            return null;
        }
    }

    public void ClearGeminiApiKey() => EncryptedGeminiApiKey = null;

    private static byte[] GetEntropy() => SHA256.HashData(Encoding.UTF8.GetBytes("CodeBoxX|GeminiApiKey|v1"));

    public void AddRecentFile(string path)
    {
        AddRecent(RecentFiles, path);
    }

    public void AddRecentProject(string path)
    {
        AddRecent(RecentProjects, path);
    }

    private static void AddRecent(List<string> items, string path)
    {
        items.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        items.Insert(0, path);
        if (items.Count > 12) items.RemoveRange(12, items.Count - 12);
    }
}
