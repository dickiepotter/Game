namespace RP.Game.Mechanics
{
    using System;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// The generic persistence framework: save and load any serialisable object as human-readable JSON,
    /// robustly. The <i>machinery</i> of persisting state lives here in Game; <i>which</i> fields a game
    /// saves is the game's business (build brief S4.1, S21.2).
    /// </summary>
    /// <remarks>
    /// <para>Two robustness rules the brief insists on (S21.2):</para>
    /// <list type="bullet">
    ///   <item><description><b>Atomic writes.</b> Write to a temporary file, then replace the real file in
    ///   one step. A crash mid-write can only damage the throwaway temp, never the existing save.</description></item>
    ///   <item><description><b>No crash on bad data.</b> A missing or corrupt file is <i>reported</i>
    ///   (return false), not thrown — a broken save must never take the game down with it.</description></item>
    /// </list>
    /// </remarks>
    public static class JsonStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// Saves <paramref name="value"/> to <paramref name="path"/> as JSON, atomically (temp file then
        /// replace), creating the directory if needed.
        /// </summary>
        public static void Save<T>(string path, T value)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp";
            string json = JsonSerializer.Serialize(value, Options);
            File.WriteAllText(tempPath, json);

            // Replace is atomic where the platform supports it; fall back to move for a first-time write.
            if (File.Exists(path)) File.Replace(tempPath, path, destinationBackupFileName: null);
            else File.Move(tempPath, path);
        }

        /// <summary>
        /// Tries to load a <typeparamref name="T"/> from <paramref name="path"/>. Returns false for a
        /// missing, unreadable, or corrupt file (with <paramref name="value"/> set to default) rather than
        /// throwing.
        /// </summary>
        public static bool TryLoad<T>(string path, out T? value)
        {
            value = default;
            try
            {
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path);
                value = JsonSerializer.Deserialize<T>(json, Options);
                return value is not null;
            }
            catch (Exception)
            {
                // Corrupt or unreadable: caller falls back to defaults / "no save".
                return false;
            }
        }

        /// <summary>
        /// The per-user data directory for <paramref name="appName"/> (e.g. <c>%AppData%\Spectre</c>), where
        /// saves and settings belong — not next to the executable (build brief S21.2).
        /// </summary>
        public static string UserDataDirectory(string appName)
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, appName);
        }
    }
}
