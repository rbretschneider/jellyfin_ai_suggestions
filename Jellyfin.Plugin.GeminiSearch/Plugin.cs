using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.GeminiSearch;

/// <summary>
/// The main plugin class for Gemini Search.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Gemini Search";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a4df60c5-6ab4-412a-8f79-2cab93fb2bc5");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the plugin configuration.
    /// </summary>
    public PluginConfiguration PluginConfiguration => Configuration;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
        // ONLY ONE PAGE - Admin Configuration (this is how Jellyfin plugins work)
        new PluginPageInfo
        {
            Name = "gemini-search-config",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.adminConfigPage.html",
            EnableInMainMenu = true,
            DisplayName = "Gemini Search Configuration"
        }
    };
    }
}