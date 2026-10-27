using Microsoft.Extensions.Logging;
using Stagecast.Core.Configuration;

namespace Stagecast.App;

/// <summary>Everything the main window needs from startup.</summary>
public sealed record StartupContext(
    string Version, AppPaths Paths, ConfigStore ConfigStore, ConfigLoadResult Config, ILoggerFactory LoggerFactory, string? StartUrl, string? SelfTest);
