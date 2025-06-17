using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.GeminiSearch;

/// <summary>
/// Configuration for the Gemini Search plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Gemini API key.
    /// </summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}