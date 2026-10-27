using Stagecast.Core.Rendering;

namespace Stagecast.Core.Presets;

public sealed record Preset(string Name, Resolution Resolution, bool BuiltIn = false);
