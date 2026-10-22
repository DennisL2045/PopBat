using System;
using System.IO;
using System.Text.Json;

namespace PopBat;

internal sealed record Preferences(bool SaveHistory = true, bool Notify = false);
internal sealed class PreferenceStore
{
    private readonly string _file;
    internal PreferenceStore(string directory) { _file = Path.Combine(directory, "preferences.json"); }
    internal Preferences Load()
    {
        try { if (File.Exists(_file) && new FileInfo(_file).Length < 4096) return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_file)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }
    internal bool Save(Preferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(preferences));
            File.Move(_file + ".tmp", _file, true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
