using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace RouteSuggest;

/// <summary>
/// Defines how a room type's weight scales with a projected resource.
/// </summary>
public class ScalingRule
{
    /// <summary>
    /// Which resource this scales with: "Gold", "Health", or "InverseHealth".
    /// Gold: modifier increases with more gold. Health: modifier increases with higher HP%.
    /// InverseHealth: modifier increases with lower HP% (e.g., rest sites are more valuable when hurt).
    /// </summary>
    public string Resource { get; set; }

    /// <summary>
    /// The resource value at which the modifier equals 1.0 (no change from base weight).
    /// For Gold: an absolute gold amount (e.g., 100). For Health/InverseHealth: a health fraction 0.0-1.0.
    /// </summary>
    public double Baseline { get; set; }
}

/// <summary>
/// Configuration for dynamic scoring: resource projections and scaling rules.
/// </summary>
public class DynamicScoringConfig
{
    /// <summary>
    /// Estimated gold change per room type (e.g., Monster: +25, Shop: -80).
    /// </summary>
    public Dictionary<MapPointType, int> GoldDeltas { get; set; } = new Dictionary<MapPointType, int>();

    /// <summary>
    /// Estimated health change per room type (e.g., Monster: -15, RestSite: +30).
    /// </summary>
    public Dictionary<MapPointType, int> HealthDeltas { get; set; } = new Dictionary<MapPointType, int>();

    /// <summary>
    /// Per-room-type scaling rules. Absent entries use no scaling (modifier = 1.0).
    /// </summary>
    public Dictionary<MapPointType, ScalingRule> ScalingRules { get; set; } = new Dictionary<MapPointType, ScalingRule>();

    /// <summary>
    /// Minimum modifier clamp (default 0.0). Prevents weights from going too negative via scaling.
    /// </summary>
    public double MinModifier { get; set; } = 0.0;

    /// <summary>
    /// Maximum modifier clamp (default 2.0). Prevents weights from scaling too high.
    /// </summary>
    public double MaxModifier { get; set; } = 2.0;
}

/// <summary>
/// Configuration for a path type including scoring weights, color, and priority.
/// </summary>
public class PathConfig
{
    /// <summary>
    /// Name for this path configuration (e.g., "Safe", "Aggressive").
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Color used to highlight this path type on the map.
    /// </summary>
    public Color Color { get; set; }

    /// <summary>
    /// Higher priority paths are rendered on top of lower priority paths when they overlap.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Scoring weights for each room type. Positive values make the room more desirable,
    /// negative values make it less desirable. Only rooms with defined weights contribute to the score.
    /// </summary>
    public Dictionary<MapPointType, int> ScoringWeights { get; set; } = new Dictionary<MapPointType, int>();

    /// <summary>
    /// Whether dynamic scoring is enabled for this path config.
    /// When enabled, room weights are scaled based on projected gold and health.
    /// </summary>
    public bool DynamicScoringEnabled { get; set; } = false;

    /// <summary>
    /// Dynamic scoring configuration with resource projections and scaling rules.
    /// </summary>
    public DynamicScoringConfig DynamicScoring { get; set; } = new DynamicScoringConfig();

    /// <summary>
    /// Calculates the total score for a given path using this configuration's scoring weights.
    /// When dynamic scoring is enabled and game state is available, weights are scaled
    /// based on projected gold and health at each node.
    /// </summary>
    /// <param name="path">List of map points representing the path to score.</param>
    /// <param name="currentGold">Current gold amount, or -1 if unavailable.</param>
    /// <param name="currentHp">Current health, or -1 if unavailable.</param>
    /// <param name="maxHp">Maximum health, or -1 if unavailable.</param>
    /// <returns>The total score, or 0 if path is null or empty.</returns>
    public double CalculateScore(List<MapPoint> path, int currentGold = -1, int currentHp = -1, int maxHp = -1)
    {
        if (path == null) return 0.0;

        bool useDynamic = DynamicScoringEnabled && currentGold >= 0 && currentHp >= 0 && maxHp > 0;

        if (!useDynamic)
        {
            double staticScore = 0.0;
            foreach (var point in path)
            {
                if (ScoringWeights.TryGetValue(point.PointType, out int weight))
                {
                    staticScore += weight;
                }
            }
            return staticScore;
        }

        double projectedGold = currentGold;
        double projectedHealth = currentHp;
        double score = 0.0;

        foreach (var point in path)
        {
            if (!ScoringWeights.TryGetValue(point.PointType, out int baseWeight))
            {
                baseWeight = 0;
            }

            double modifier = 1.0;

            if (DynamicScoring.ScalingRules.TryGetValue(point.PointType, out var rule))
            {
                double healthPct = projectedHealth / maxHp;

                switch (rule.Resource)
                {
                    case "Gold":
                        if (rule.Baseline > 0)
                            modifier = projectedGold / rule.Baseline;
                        break;
                    case "Health":
                        if (rule.Baseline > 0)
                            modifier = healthPct / rule.Baseline;
                        break;
                    case "InverseHealth":
                        if (rule.Baseline < 1.0)
                            modifier = (1.0 - healthPct) / (1.0 - rule.Baseline);
                        break;
                }

                modifier = Math.Clamp(modifier, DynamicScoring.MinModifier, DynamicScoring.MaxModifier);
            }

            score += baseWeight * modifier;

            // Project resources forward
            if (DynamicScoring.GoldDeltas.TryGetValue(point.PointType, out int goldDelta))
                projectedGold += goldDelta;
            projectedGold = Math.Max(projectedGold, 0);

            if (DynamicScoring.HealthDeltas.TryGetValue(point.PointType, out int healthDelta))
                projectedHealth += healthDelta;
            projectedHealth = Math.Clamp(projectedHealth, 1, maxHp);
        }

        return score;
    }
}

/// <summary>
/// Enum for controlling how many paths to highlight: pick one from optimal paths, or highlight all optimal paths.
/// </summary>
public enum HighlightType
{
    /// <summary>Highlight one path from among optimal paths.</summary>
    One,
    /// <summary>Highlight all paths with the best score.</summary>
    All
}

/// <summary>
/// Main mod class for RouteSuggest. Provides optimal path suggestions on the map with multiple strategies.
/// </summary>
[ModInitializer("ModLoaded")]
public static class RouteSuggest
{
    /// <summary>
    /// Current run state, set when a run starts.
    /// </summary>
    public static RunState RunState { get; private set; }

    /// <summary>
    /// Dictionary of calculated paths for each configured path type.
    /// Key is the path config name, value is a list of calculated paths (one or multiple depending on HighlightType).
    /// </summary>
    public static Dictionary<string, List<List<MapPoint>>> CalculatedPaths { get; private set; } = new Dictionary<string, List<List<MapPoint>>>();

    /// <summary>
    /// List of configured path types. Can be modified at runtime or loaded from config file.
    /// Defaults contain Safe (gold) and Aggressive (red) path types.
    /// </summary>
    public static List<PathConfig> PathConfigs { get; private set; }

    /// <summary>
    /// Controls whether to highlight one optimal path (One) or all optimal paths (All).
    /// </summary>
    public static HighlightType CurrentHighlightType { get; set; } = HighlightType.One;

    /// <summary>
    /// Default path configurations used as fallback and for reset functionality.
    /// </summary>
    private static readonly DynamicScoringConfig DefaultDynamicScoringConfig = new DynamicScoringConfig
    {
        GoldDeltas = new Dictionary<MapPointType, int>
        {
            { MapPointType.Monster, 25 },
            { MapPointType.Elite, 40 },
            { MapPointType.Shop, -80 },
            { MapPointType.Treasure, 0 },
            { MapPointType.Unknown, 10 }
        },
        HealthDeltas = new Dictionary<MapPointType, int>
        {
            { MapPointType.Monster, -15 },
            { MapPointType.Elite, -30 },
            { MapPointType.RestSite, 30 },
            { MapPointType.Unknown, -5 }
        },
        ScalingRules = new Dictionary<MapPointType, ScalingRule>
        {
            { MapPointType.Shop, new ScalingRule { Resource = "Gold", Baseline = 100 } },
            { MapPointType.Monster, new ScalingRule { Resource = "Health", Baseline = 0.7 } },
            { MapPointType.Elite, new ScalingRule { Resource = "Health", Baseline = 0.7 } },
            { MapPointType.RestSite, new ScalingRule { Resource = "InverseHealth", Baseline = 0.5 } }
        },
        MinModifier = 0.0,
        MaxModifier = 2.0
    };

    private static readonly List<PathConfig> DefaultPathConfigs = new List<PathConfig>
    {
        new PathConfig
        {
            Name = "Safe",
            Color = new Color(1f, 0.84f, 0f, 1f), // Gold
            Priority = 100, // Higher priority - shown over aggressive path
            ScoringWeights = new Dictionary<MapPointType, int>
            {
                { MapPointType.RestSite, 1 },
                { MapPointType.Treasure, 1 },
                { MapPointType.Shop, 1 },
                { MapPointType.Monster, -1 },
                { MapPointType.Elite, -3 },
                { MapPointType.Unknown, 0 }
            },
            DynamicScoringEnabled = false,
            DynamicScoring = DefaultDynamicScoringConfig
        },
        new PathConfig
        {
            Name = "Aggressive",
            Color = new Color(1f, 0f, 0f, 1f), // Red
            Priority = 50, // Lower priority - safe path shown over this
            ScoringWeights = new Dictionary<MapPointType, int>
            {
                { MapPointType.RestSite, 1 },
                { MapPointType.Treasure, 1 },
                { MapPointType.Shop, 1 },
                { MapPointType.Monster, 2 },
                { MapPointType.Elite, 3 },
                { MapPointType.Unknown, 2 }
            },
            DynamicScoringEnabled = false,
            DynamicScoring = DefaultDynamicScoringConfig
        }
    };

    /// <summary>
    /// Stores original colors and scales for each highlighted TextureRect to restore later.
    /// </summary>
    private static readonly Dictionary<TextureRect, (Color color, Vector2 scale)> OriginalTickProperties = new Dictionary<TextureRect, (Color, Vector2)>();

    /// <summary>
    /// Cached reflection field for accessing the map screen's internal paths dictionary.
    /// </summary>
    private static FieldInfo _pathsField;

    /// <summary>
    /// Whether map screen reflection has been successfully initialized.
    /// </summary>
    private static bool _reflectionInitialized = false;

    /// <summary>
    /// Cached property/field accessors for player gold, current HP, and max HP.
    /// Discovered via reflection on RunState or related player objects.
    /// </summary>
    private static Func<RunState, int> _getGold;
    private static Func<RunState, int> _getCurrentHp;
    private static Func<RunState, int> _getMaxHp;
    private static bool _runStateReflectionInitialized = false;

    /// <summary>
    /// Flag indicating if a highlight request is pending for when the map screen opens.
    /// </summary>
    private static bool _pendingHighlight = false;

    /// <summary>
    /// Stores the path to the config file that was used for loading.
    /// Used for saving configuration back to the same location.
    /// </summary>
    private static string _configFilePath = null;

    /// <summary>
    /// Logs a message with a timestamp prefix.
    /// </summary>
    /// <param name="message">The message to log.</param>
    private static void LogWithTimestamp(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Log.Warn($"[{timestamp}] RouteSuggest: {message}");
    }

    /// <summary>
    /// Called when the mod is loaded. Initializes configuration, subscribes to game events,
    /// and sets up reflection for map highlighting.
    /// </summary>
    public static void ModLoaded()
    {
        LogWithTimestamp("Mod loaded");

        // Initialize PathConfigs from defaults
        PathConfigs = new List<PathConfig>();
        ResetToDefault();

        // Load configuration from file if available
        LoadConfig();

        // Pretty print the current path configurations
        PrintPathConfigs();

        // Subscribe to game events
        var manager = RunManager.Instance;
        manager.RunStarted += OnRunStarted;
        manager.ActEntered += OnActEntered;
        manager.RoomEntered += OnRoomEntered;
        manager.RoomExited += OnRoomExited;

        // Initialize reflection for path highlighting
        InitializeReflection();

        // Register with ModConfig for GUI settings (via reflection)
        DeferredRegisterModConfig();
    }

    /// <summary>
    /// Deferred registration with ModConfig to ensure all mods are loaded first.
    /// Uses reflection for zero-dependency integration.
    /// </summary>
    static void DeferredRegisterModConfig()
    {
        // Wait one frame for all mods to load
        var tree = (SceneTree)Engine.GetMainLoop();
        Action callback = null;
        callback = () =>
        {
            tree.ProcessFrame -= callback;
            RegisterModConfigViaReflection();
        };
        tree.ProcessFrame += callback;
    }

    /// <summary>
    /// Registers path configurations with ModConfig using reflection.
    /// This allows ModConfig integration without a hard DLL dependency.
    /// </summary>
    static void RegisterModConfigViaReflection()
    {
        try
        {
            // Check if ModConfig is available
            var apiType = Type.GetType("ModConfig.ModConfigApi, ModConfig");
            var entryType = Type.GetType("ModConfig.ConfigEntry, ModConfig");
            var configType = Type.GetType("ModConfig.ConfigType, ModConfig");

            if (apiType == null || entryType == null || configType == null)
            {
                LogWithTimestamp("ModConfig not found, skipping GUI registration");
                return;
            }

            var entries = new List<object>();

            // Helper to create ConfigEntry via reflection
            object MakeEntry(string key, string label, object type,
                object defaultValue = null, float min = 0, float max = 100, float step = 1,
                string format = "F0", string[] options = null,
                Dictionary<string, string> labels = null,
                Dictionary<string, string> descriptions = null,
                Action<object> onChanged = null)
            {
                var entry = Activator.CreateInstance(entryType);
                entryType.GetProperty("Key")?.SetValue(entry, key);
                entryType.GetProperty("Label")?.SetValue(entry, label);
                entryType.GetProperty("Type")?.SetValue(entry, type);
                if (defaultValue != null)
                    entryType.GetProperty("DefaultValue")?.SetValue(entry, defaultValue);
                entryType.GetProperty("Min")?.SetValue(entry, min);
                entryType.GetProperty("Max")?.SetValue(entry, max);
                entryType.GetProperty("Step")?.SetValue(entry, step);
                entryType.GetProperty("Format")?.SetValue(entry, format);
                if (options != null)
                    entryType.GetProperty("Options")?.SetValue(entry, options);
                if (labels != null)
                    entryType.GetProperty("Labels")?.SetValue(entry, labels);
                if (descriptions != null)
                    entryType.GetProperty("Descriptions")?.SetValue(entry, descriptions);
                if (onChanged != null)
                    entryType.GetProperty("OnChanged")?.SetValue(entry, onChanged);
                return entry;
            }

            // Helper to get ConfigType enum value
            object GetConfigType(string name) => Enum.Parse(configType, name);

            entries.Add(MakeEntry("", "General", GetConfigType("Header"),
                labels: new() { { "zhs", "通用" } }));

            // Reset to defaults logic
            entries.Add(MakeEntry("__reset_default", "Reset to defaults",
                GetConfigType("Slider"),
                defaultValue: 1f,
                min: 0, max: 1, step: 1, format: "F0",
                labels: new() { { "zhs", "重置为默认值" } },
                descriptions: new() { { "en", "Slide to 1 to reset all configurations to default" }, { "zhs", "滑到 1 以重置所有配置为默认值" } },
                onChanged: (value) =>
                {
                    if ((int)(float)value == 1)
                    {
                        ResetToDefault();
                        SaveAndUpdatePath();

                        // Re-register to refresh UI
                        RegisterModConfigViaReflection();
                    }
                }));

            // Highlight type selector
            entries.Add(MakeEntry("highlight_type", "Highlight Type",
                GetConfigType("Dropdown"),
                defaultValue: CurrentHighlightType.ToString(),
                options: new[] { "One", "All" },
                labels: new() { { "zhs", "高亮类型" } },
                descriptions: new() { { "en", "Pick one path from optimal paths (One) or highlight all optimal paths (All)" }, { "zhs", "从最优路径中选择一条 (One) 或高亮所有最优路径 (All)" } },
                onChanged: (value) =>
                {
                    if (Enum.TryParse<HighlightType>((string)value, out var newType))
                    {
                        CurrentHighlightType = newType;
                        SaveAndUpdatePath();
                    }
                }));


            entries.Add(MakeEntry("", "", GetConfigType("Separator")));

            // Path Management section at the top
            entries.Add(MakeEntry("", "Path Management", GetConfigType("Header"),
                labels: new() { { "zhs", "路径管理" } }));

            // Slider to add new path (0->1 triggers add)
            entries.Add(MakeEntry("__add_path", "Add New Path (slide to 1)",
                GetConfigType("Slider"),
                defaultValue: 0f,
                min: 0, max: 1, step: 1, format: "F0",
                labels: new() { { "zhs", "添加新路径 (滑到 1)" } },
                descriptions: new() { { "en", "Slide to 1 to add a new path configuration" }, { "zhs", "滑到 1 以添加新的路径配置" } },
                onChanged: (value) =>
                {
                    if ((int)(float)value == 1)
                    {
                        var newConfig = new PathConfig
                        {
                            Name = $"Path{PathConfigs.Count + 1}",
                            Color = new Color(1f, 1f, 1f, 1f),
                            Priority = 50,
                            ScoringWeights = new Dictionary<MapPointType, int>(),
                            DynamicScoringEnabled = false,
                            DynamicScoring = new DynamicScoringConfig
                            {
                                GoldDeltas = new Dictionary<MapPointType, int>(DefaultDynamicScoringConfig.GoldDeltas),
                                HealthDeltas = new Dictionary<MapPointType, int>(DefaultDynamicScoringConfig.HealthDeltas),
                                ScalingRules = new Dictionary<MapPointType, ScalingRule>(DefaultDynamicScoringConfig.ScalingRules),
                                MinModifier = DefaultDynamicScoringConfig.MinModifier,
                                MaxModifier = DefaultDynamicScoringConfig.MaxModifier
                            }
                        };
                        PathConfigs.Add(newConfig);
                        SaveAndUpdatePath();

                        // Re-register to refresh UI
                        RegisterModConfigViaReflection();
                    }
                }));

            entries.Add(MakeEntry("", "", GetConfigType("Separator")));

            // Add configuration for each path
            for (int i = 0; i < PathConfigs.Count; i++)
            {
                var config = PathConfigs[i];
                var pathIndex = i;

                // Path header with remove slider
                entries.Add(MakeEntry("", $"Path {i + 1}",
                    GetConfigType("Header"),
                    labels: new() { { "zhs", $"路径 {i + 1}" } }
                ));

                // Remove this path slider (0 = keep, 1 = remove)
                entries.Add(MakeEntry($"path_{i}_remove", "Remove Path (0=keep, 1=remove)",
                    GetConfigType("Slider"),
                    defaultValue: 0f,
                    min: 0, max: 1, step: 1, format: "F0",
                    labels: new() { { "zhs", "删除路径 (0=保留, 1=删除)" } },
                    descriptions: new() { { "en", "Slide to 1 to remove this path configuration" }, { "zhs", "滑到 1 以删除此路径配置" } },
                    onChanged: (value) =>
                    {
                        if ((int)(float)value == 1)
                        {
                            PathConfigs.RemoveAt(pathIndex);
                            SaveAndUpdatePath();

                            // Re-register to refresh UI
                            RegisterModConfigViaReflection();
                        }
                    }));

                // Name
                entries.Add(MakeEntry($"path_{i}_name", "Name", GetConfigType("TextInput"),
                    defaultValue: config.Name,
                    labels: new() { { "zhs", "名称" } },
                    descriptions: new() { { "en", "The name of this path" }, { "zhs", "此路径的名称" } },
                    onChanged: (value) =>
                    {
                        config.Name = (string)value;
                        SaveAndUpdatePath();
                    }));

                // Color (hex input)
                entries.Add(MakeEntry($"path_{i}_color", "Color (hex, e.g., #FFD700)",
                    GetConfigType("TextInput"),
                    defaultValue: $"#{config.Color.ToHtml(false)}",
                    labels: new() { { "zhs", "颜色 (十六进制, 如 #FFD700)" } },
                    descriptions: new() { { "en", "Hex color code for path highlighting" }, { "zhs", "用于路径高亮的十六进制颜色代码" } },
                    onChanged: (value) =>
                    {
                        config.Color = ParseColor((string)value);
                        SaveAndUpdatePath();
                    }));

                // Priority
                entries.Add(MakeEntry($"path_{i}_priority", "Priority (higher = on top)",
                    GetConfigType("Slider"),
                    defaultValue: (float)config.Priority,
                    min: 0, max: 200, step: 10, format: "F0",
                    labels: new() { { "zhs", "优先级 (越高越靠前)" } },
                    descriptions: new() { { "en", "Higher priority paths are rendered on top of lower priority paths" }, { "zhs", "优先级高的路径会覆盖优先级低的路径" } },
                    onChanged: (value) =>
                    {
                        config.Priority = (int)(float)value;
                        SaveAndUpdatePath();
                    }));

                // Dynamic Scoring toggle
                entries.Add(MakeEntry($"path_{i}_dynamic_scoring", "Dynamic Scoring (0=off, 1=on)",
                    GetConfigType("Slider"),
                    defaultValue: config.DynamicScoringEnabled ? 1f : 0f,
                    min: 0, max: 1, step: 1, format: "F0",
                    labels: new() { { "zhs", "动态评分 (0=关闭, 1=开启)" } },
                    descriptions: new() { { "en", "Scale room weights based on projected gold and health. Configure details in RouteSuggestConfig.json." }, { "zhs", "根据预测的金币和生命值缩放房间权重。在 RouteSuggestConfig.json 中配置详情。" } },
                    onChanged: (value) =>
                    {
                        config.DynamicScoringEnabled = (int)(float)value == 1;
                        SaveAndUpdatePath();
                    }));

                // Scoring weights section header
                entries.Add(MakeEntry("", "Scoring Weights", GetConfigType("Header"),
                    labels: new() { { "zhs", "评分权重" } }));

                // Add weights for each room type
                var roomTypes = new[] { MapPointType.RestSite, MapPointType.Treasure, MapPointType.Shop,
                    MapPointType.Monster, MapPointType.Elite, MapPointType.Unknown, MapPointType.Boss };
                foreach (var roomType in roomTypes)
                {
                    if (!config.ScoringWeights.TryGetValue(roomType, out int weight))
                        weight = 0;

                    var capturedRoomType = roomType;
                    var roomLabels = new Dictionary<string, string>
                    {
                        { "en", roomType.ToString() }
                    };
                    var roomDescriptions = new Dictionary<string, string>
                    {
                        { "en", $"Scoring weight for {roomType} rooms" }
                    };

                    // Add Chinese translations for room types
                    switch (roomType)
                    {
                        case MapPointType.RestSite:
                            roomLabels["zhs"] = "休息处";
                            roomDescriptions["zhs"] = "休息处房间的评分权重";
                            break;
                        case MapPointType.Treasure:
                            roomLabels["zhs"] = "宝箱";
                            roomDescriptions["zhs"] = "宝箱房间的评分权重";
                            break;
                        case MapPointType.Shop:
                            roomLabels["zhs"] = "商店";
                            roomDescriptions["zhs"] = "商店房间的评分权重";
                            break;
                        case MapPointType.Monster:
                            roomLabels["zhs"] = "普通敌人";
                            roomDescriptions["zhs"] = "普通敌人房间的评分权重";
                            break;
                        case MapPointType.Elite:
                            roomLabels["zhs"] = "精英敌人";
                            roomDescriptions["zhs"] = "精英敌人房间的评分权重";
                            break;
                        case MapPointType.Unknown:
                            roomLabels["zhs"] = "未知";
                            roomDescriptions["zhs"] = "未知房间的评分权重";
                            break;
                        case MapPointType.Boss:
                            roomLabels["zhs"] = "Boss";
                            roomDescriptions["zhs"] = "Boss 房间的评分权重";
                            break;
                    }

                    entries.Add(MakeEntry($"path_{i}_weight_{roomType}", roomType.ToString(),
                        GetConfigType("Slider"),
                        defaultValue: (float)weight,
                        min: -100, max: 100, step: 1, format: "F0",
                        labels: roomLabels,
                        descriptions: roomDescriptions,
                        onChanged: (value) =>
                        {
                            config.ScoringWeights[capturedRoomType] = (int)(float)value;
                            SaveAndUpdatePath();
                        }));
                }

                entries.Add(MakeEntry("", "", GetConfigType("Separator")));
            }

            // convert to entryType[]
            var entriesArray = Array.CreateInstance(entryType, entries.Count());
            for (int i = 0; i < entries.Count(); i++)
            {
                entriesArray.SetValue(entries[i], i);
            }

            // Register with ModConfig via reflection
            var registerMethod = apiType.GetMethod("Register",
                new[] { typeof(string), typeof(string), entryType.MakeArrayType() });
            registerMethod!.Invoke(null, new object[] { "RouteSuggest", "RouteSuggest", entriesArray });

            LogWithTimestamp($"Registered {entries.Count()} entries with ModConfig (via reflection)");

            var method = apiType.GetMethod("SetValue")!;
            method.Invoke(null, new object[] { "RouteSuggest", "__reset_default", 0f });
        }
        catch (Exception ex)
        {
            LogWithTimestamp($"Failed to register with ModConfig: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines the config file path to use.
    /// Priority: 1) mods/RouteSuggestConfig.json if exists, 2) same directory as RouteSuggest.dll (recursive search), 3) mods/RouteSuggestConfig.json as fallback
    /// </summary>
    /// <returns>The determined config file path.</returns>
    static string GetConfigFilePath()
    {
        string executablePath = OS.GetExecutablePath();
        string directoryName = Path.GetDirectoryName(executablePath);
        string modsPath = Path.Combine(directoryName, "mods");
        string modsConfigPath = Path.Combine(modsPath, "RouteSuggestConfig.json");

        LogWithTimestamp($"Determining config file path. Mods directory: {modsPath}");

        // Priority 1: Check if mods/RouteSuggestConfig.json exists
        LogWithTimestamp($"Priority 1: Checking for config at {modsConfigPath}");
        if (File.Exists(modsConfigPath))
        {
            LogWithTimestamp($"Found config at {modsConfigPath} (Priority 1)");
            return modsConfigPath;
        }
        LogWithTimestamp($"Config not found at {modsConfigPath}");

        // Priority 2: Find RouteSuggest.dll recursively and use its directory
        if (Directory.Exists(modsPath))
        {
            LogWithTimestamp("Priority 2: Searching for RouteSuggest.dll recursively in mods folder");
            try
            {
                string[] dllFiles = Directory.GetFiles(modsPath, "RouteSuggest.dll", SearchOption.AllDirectories);
                if (dllFiles.Length > 0)
                {
                    string dllPath = dllFiles[0];
                    string dllDirectory = Path.GetDirectoryName(dllPath);
                    string dllConfigPath = Path.Combine(dllDirectory, "RouteSuggestConfig.json");
                    LogWithTimestamp($"Found RouteSuggest.dll at {dllPath}");
                    LogWithTimestamp($"Using config path: {dllConfigPath} (Priority 2)");
                    return dllConfigPath;
                }
                else
                {
                    LogWithTimestamp("RouteSuggest.dll not found in mods folder or subdirectories");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                LogWithTimestamp($"Permission denied while searching for RouteSuggest.dll: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogWithTimestamp($"Error searching for RouteSuggest.dll: {ex.Message}");
            }
        }
        else
        {
            LogWithTimestamp($"Mods directory does not exist: {modsPath}");
        }

        // Priority 3: Fall back to mods/RouteSuggestConfig.json
        LogWithTimestamp($"Priority 3: Falling back to default path {modsConfigPath}");
        return modsConfigPath;
    }

    /// <summary>
    /// Saves the current PathConfigs to RouteSuggestConfig.json.
    /// Called automatically when settings are changed via ModConfig.
    /// Uses the path determined during LoadConfig.
    /// </summary>
    static void SaveConfiguration()
    {
        try
        {
            // Use remembered path
            string configPath = _configFilePath;

            var configData = new ConfigFile
            {
                SchemaVersion = 3,
                HighlightType = CurrentHighlightType.ToString(),
                PathConfigs = new List<PathConfigEntry>()
            };

            foreach (var config in PathConfigs)
            {
                var entry = new PathConfigEntry
                {
                    Name = config.Name,
                    Color = $"#{config.Color.ToHtml(false)}",
                    Priority = config.Priority,
                    ScoringWeights = new Dictionary<string, int>(),
                    DynamicScoringEnabled = config.DynamicScoringEnabled
                };

                foreach (var weight in config.ScoringWeights)
                {
                    entry.ScoringWeights[weight.Key.ToString()] = weight.Value;
                }

                // Serialize dynamic scoring config
                if (config.DynamicScoring != null)
                {
                    var dsEntry = new DynamicScoringConfigEntry
                    {
                        GoldDeltas = new Dictionary<string, int>(),
                        HealthDeltas = new Dictionary<string, int>(),
                        ScalingRules = new Dictionary<string, ScalingRuleEntry>(),
                        MinModifier = config.DynamicScoring.MinModifier,
                        MaxModifier = config.DynamicScoring.MaxModifier
                    };

                    foreach (var kvp in config.DynamicScoring.GoldDeltas)
                        dsEntry.GoldDeltas[kvp.Key.ToString()] = kvp.Value;
                    foreach (var kvp in config.DynamicScoring.HealthDeltas)
                        dsEntry.HealthDeltas[kvp.Key.ToString()] = kvp.Value;
                    foreach (var kvp in config.DynamicScoring.ScalingRules)
                    {
                        dsEntry.ScalingRules[kvp.Key.ToString()] = new ScalingRuleEntry
                        {
                            Resource = kvp.Value.Resource,
                            Baseline = kvp.Value.Baseline
                        };
                    }

                    entry.DynamicScoring = dsEntry;
                }

                configData.PathConfigs.Add(entry);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };

            string json = JsonSerializer.Serialize(configData, options);
            File.WriteAllText(configPath, json);

            LogWithTimestamp($"Configuration saved to {configPath}");
        }
        catch (Exception ex)
        {
            LogWithTimestamp($"Failed to save configuration: {ex.Message}");
        }
    }

    /// <summary>
    /// Resets to the default configuration.
    /// Creates deep copies of default configs to avoid modifying the originals.
    /// </summary>
    public static void ResetToDefault()
    {
        CurrentHighlightType = HighlightType.One;

        PathConfigs.Clear();
        foreach (var defaultConfig in DefaultPathConfigs)
        {
            var config = new PathConfig
            {
                Name = defaultConfig.Name,
                Color = defaultConfig.Color,
                Priority = defaultConfig.Priority,
                ScoringWeights = new Dictionary<MapPointType, int>(defaultConfig.ScoringWeights),
                DynamicScoringEnabled = defaultConfig.DynamicScoringEnabled,
                DynamicScoring = new DynamicScoringConfig
                {
                    GoldDeltas = new Dictionary<MapPointType, int>(defaultConfig.DynamicScoring.GoldDeltas),
                    HealthDeltas = new Dictionary<MapPointType, int>(defaultConfig.DynamicScoring.HealthDeltas),
                    ScalingRules = new Dictionary<MapPointType, ScalingRule>(defaultConfig.DynamicScoring.ScalingRules),
                    MinModifier = defaultConfig.DynamicScoring.MinModifier,
                    MaxModifier = defaultConfig.DynamicScoring.MaxModifier
                }
            };
            PathConfigs.Add(config);
        }
        LogWithTimestamp("Reset to default path configurations");
    }

    /// <summary>
    /// Loads path configurations from RouteSuggestConfig.json if it exists.
    /// Falls back to default PathConfigs if file is missing or invalid.
    /// Remembers the config path for subsequent saves.
    /// </summary>
    static void LoadConfig()
    {
        try
        {
            string configPath = GetConfigFilePath();

            // Remember this path for saving
            _configFilePath = configPath;

            if (!File.Exists(configPath))
            {
                LogWithTimestamp($"Config file not found at {configPath}, using default path configs");
                return;
            }

            string json = File.ReadAllText(configPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var configData = JsonSerializer.Deserialize<ConfigFile>(json, options);

            if (configData?.SchemaVersion < 1 || configData?.SchemaVersion > 3)
            {
                LogWithTimestamp($"Unsupported schema version {configData?.SchemaVersion}, using defaults");
                return;
            }

            // Load highlight type if present
            if (!string.IsNullOrEmpty(configData.HighlightType))
            {
                if (Enum.TryParse<HighlightType>(configData.HighlightType, out var loadedType))
                {
                    CurrentHighlightType = loadedType;
                    LogWithTimestamp($"Loaded highlight type: {loadedType}");
                }
            }

            if (configData.PathConfigs == null)
            {
                LogWithTimestamp("No path configs found in config file, using defaults");
                return;
            }

            // Clear and replace with loaded configs
            PathConfigs.Clear();
            foreach (var configEntry in configData.PathConfigs)
            {
                var config = new PathConfig
                {
                    Name = configEntry.Name,
                    Priority = configEntry.Priority,
                    Color = ParseColor(configEntry.Color),
                    ScoringWeights = ParseScoringWeights(configEntry.ScoringWeights),
                    DynamicScoringEnabled = configEntry.DynamicScoringEnabled ?? false
                };

                // Parse dynamic scoring config if present
                if (configEntry.DynamicScoring != null)
                {
                    var ds = configEntry.DynamicScoring;
                    config.DynamicScoring = new DynamicScoringConfig
                    {
                        GoldDeltas = ParseIntDeltas(ds.GoldDeltas),
                        HealthDeltas = ParseIntDeltas(ds.HealthDeltas),
                        ScalingRules = ParseScalingRules(ds.ScalingRules),
                        MinModifier = ds.MinModifier ?? 0.0,
                        MaxModifier = ds.MaxModifier ?? 2.0
                    };
                    LogWithTimestamp($"  Dynamic scoring: enabled={config.DynamicScoringEnabled}, " +
                                   $"gold_deltas={config.DynamicScoring.GoldDeltas.Count}, " +
                                   $"health_deltas={config.DynamicScoring.HealthDeltas.Count}, " +
                                   $"scaling_rules={config.DynamicScoring.ScalingRules.Count}");
                }

                PathConfigs.Add(config);
                LogWithTimestamp($"Loaded path config '{config.Name}' from file");
            }

            LogWithTimestamp($"Successfully loaded {PathConfigs.Count} path configs from {configPath}");
        }
        catch (Exception ex)
        {
            LogWithTimestamp($"Failed to load config file: {ex.Message}, using defaults");
        }
    }

    /// <summary>
    /// Pretty prints all current path configurations for debugging.
    /// </summary>
    static void PrintPathConfigs()
    {
        LogWithTimestamp("Current Path Configurations:");
        LogWithTimestamp("==========================================");

        foreach (var config in PathConfigs)
        {
            LogWithTimestamp($"  Path: {config.Name}");
            LogWithTimestamp($"    Priority: {config.Priority}");
            LogWithTimestamp($"    Color: R={config.Color.R:F2}, G={config.Color.G:F2}, B={config.Color.B:F2}, A={config.Color.A:F2}");
            LogWithTimestamp($"    Scoring Weights:");

            foreach (var weight in config.ScoringWeights.OrderBy(w => w.Key.ToString()))
            {
                LogWithTimestamp($"      {weight.Key}: {weight.Value:+0;-0;0}");
            }

            LogWithTimestamp("");
        }

        LogWithTimestamp($"Total path types configured: {PathConfigs.Count}");
        LogWithTimestamp("==========================================");
    }

    /// <summary>
    /// Parses a color string in hex format (#RGB or #RGBA) into a Godot Color.
    /// </summary>
    /// <param name="colorStr">Hex color string, e.g., "#FFD700" or "#FF0000FF"</param>
    /// <returns>Parsed Color, or white if parsing fails.</returns>
    static Color ParseColor(string colorStr)
    {
        if (string.IsNullOrEmpty(colorStr))
            return new Color(1f, 1f, 1f, 1f);

        // Support hex format like "#FFD700" or "#FFD700FF"
        if (colorStr.StartsWith("#"))
        {
            colorStr = colorStr.Substring(1);
            if (colorStr.Length == 6)
            {
                // RGB format
                float r = Convert.ToInt32(colorStr.Substring(0, 2), 16) / 255f;
                float g = Convert.ToInt32(colorStr.Substring(2, 2), 16) / 255f;
                float b = Convert.ToInt32(colorStr.Substring(4, 2), 16) / 255f;
                return new Color(r, g, b, 1f);
            }
            else if (colorStr.Length == 8)
            {
                // RGBA format
                float r = Convert.ToInt32(colorStr.Substring(0, 2), 16) / 255f;
                float g = Convert.ToInt32(colorStr.Substring(2, 2), 16) / 255f;
                float b = Convert.ToInt32(colorStr.Substring(4, 2), 16) / 255f;
                float a = Convert.ToInt32(colorStr.Substring(6, 2), 16) / 255f;
                return new Color(r, g, b, a);
            }
        }

        // Fallback: return white
        return new Color(1f, 1f, 1f, 1f);
    }

    /// <summary>
    /// Parses scoring weights from the config file's string-keyed dictionary to MapPointType keys.
    /// </summary>
    /// <param name="weightsDict">Dictionary with string keys from JSON.</param>
    /// <returns>Dictionary with MapPointType keys, containing valid entries only.</returns>
    static Dictionary<MapPointType, int> ParseScoringWeights(Dictionary<string, int> weightsDict)
    {
        var result = new Dictionary<MapPointType, int>();
        if (weightsDict == null) return result;

        foreach (var kvp in weightsDict)
        {
            if (Enum.TryParse<MapPointType>(kvp.Key, out var pointType))
            {
                result[pointType] = kvp.Value;
            }
            else
            {
                LogWithTimestamp($"Unknown MapPointType '{kvp.Key}' in config");
            }
        }
        return result;
    }

    /// <summary>
    /// Parses a string-keyed int dictionary into a MapPointType-keyed int dictionary.
    /// Used for gold_deltas and health_deltas in dynamic scoring config.
    /// </summary>
    static Dictionary<MapPointType, int> ParseIntDeltas(Dictionary<string, int> dict)
    {
        var result = new Dictionary<MapPointType, int>();
        if (dict == null) return result;

        foreach (var kvp in dict)
        {
            if (Enum.TryParse<MapPointType>(kvp.Key, out var pointType))
                result[pointType] = kvp.Value;
        }
        return result;
    }

    /// <summary>
    /// Parses scaling rules from string-keyed DTO dictionary into MapPointType-keyed ScalingRule dictionary.
    /// </summary>
    static Dictionary<MapPointType, ScalingRule> ParseScalingRules(Dictionary<string, ScalingRuleEntry> dict)
    {
        var result = new Dictionary<MapPointType, ScalingRule>();
        if (dict == null) return result;

        foreach (var kvp in dict)
        {
            if (Enum.TryParse<MapPointType>(kvp.Key, out var pointType))
            {
                result[pointType] = new ScalingRule
                {
                    Resource = kvp.Value.Resource,
                    Baseline = kvp.Value.Baseline
                };
            }
        }
        return result;
    }

    /// <summary>
    /// Represents the root structure of the RouteSuggestConfig.json file.
    /// </summary>
    private class ConfigFile
    {
        /// <summary>
        /// Schema version for config file compatibility. Supported versions: 1 and 2.
        /// Version 2 adds highlight_type support.
        /// </summary>
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Controls whether to highlight one optimal path (One) or all optimal paths (All).
        /// </summary>
        [JsonPropertyName("highlight_type")]
        public string HighlightType { get; set; }

        /// <summary>
        /// List of path configurations to load.
        /// </summary>
        [JsonPropertyName("path_configs")]
        public List<PathConfigEntry> PathConfigs { get; set; }
    }

    /// <summary>
    /// Represents a single path configuration entry in the JSON config file.
    /// </summary>
    private class PathConfigEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("color")]
        public string Color { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("scoring_weights")]
        public Dictionary<string, int> ScoringWeights { get; set; }

        [JsonPropertyName("dynamic_scoring_enabled")]
        public bool? DynamicScoringEnabled { get; set; }

        [JsonPropertyName("dynamic_scoring")]
        public DynamicScoringConfigEntry DynamicScoring { get; set; }
    }

    private class DynamicScoringConfigEntry
    {
        [JsonPropertyName("gold_deltas")]
        public Dictionary<string, int> GoldDeltas { get; set; }

        [JsonPropertyName("health_deltas")]
        public Dictionary<string, int> HealthDeltas { get; set; }

        [JsonPropertyName("scaling_rules")]
        public Dictionary<string, ScalingRuleEntry> ScalingRules { get; set; }

        [JsonPropertyName("min_modifier")]
        public double? MinModifier { get; set; }

        [JsonPropertyName("max_modifier")]
        public double? MaxModifier { get; set; }
    }

    private class ScalingRuleEntry
    {
        [JsonPropertyName("resource")]
        public string Resource { get; set; }

        [JsonPropertyName("baseline")]
        public double Baseline { get; set; }
    }

    /// <summary>
    /// Initializes reflection to access the map screen's internal _paths field.
    /// Required for highlighting path segments on the UI.
    /// </summary>
    static void InitializeReflection()
    {
        try
        {
            var mapScreenType = typeof(NMapScreen);
            _pathsField = mapScreenType.GetField("_paths", BindingFlags.NonPublic | BindingFlags.Instance);
            _reflectionInitialized = _pathsField != null;
            if (_reflectionInitialized)
            {
                LogWithTimestamp("Reflection initialized successfully");
            }
            else
            {
                LogWithTimestamp("Failed to initialize reflection - _paths field not found");
            }
        }
        catch (Exception ex)
        {
            LogWithTimestamp($"Error initializing reflection: {ex.Message}");
            _reflectionInitialized = false;
        }
    }

    /// <summary>
    /// Initializes reflection accessors for RunState properties: Gold, CurrentHp, MaxHp.
    /// Tries direct properties on RunState first, then looks through Player/LocalPlayer objects.
    /// Falls back gracefully if properties cannot be found.
    /// </summary>
    static void InitializeRunStateReflection(RunState runState)
    {
        if (_runStateReflectionInitialized) return;

        try
        {
            var rsType = runState.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // Log all properties for discovery
            LogWithTimestamp("Discovering RunState properties:");
            foreach (var prop in rsType.GetProperties(flags))
            {
                LogWithTimestamp($"  RunState.{prop.Name} : {prop.PropertyType.Name}");
            }

            // Try to find Gold directly on RunState
            _getGold = BuildIntGetter(rsType, "Gold", flags)
                    ?? BuildIntGetter(rsType, "CurrentGold", flags);

            // Try to find HP directly on RunState
            _getCurrentHp = BuildIntGetter(rsType, "CurrentHp", flags);
            _getMaxHp = BuildIntGetter(rsType, "MaxHp", flags);

            // If not found directly, try through Player or LocalPlayer property
            if (_getGold == null || _getCurrentHp == null || _getMaxHp == null)
            {
                LogWithTimestamp("Some properties not found on RunState directly, searching via Player...");

                var playerProp = rsType.GetProperty("Player", flags)
                              ?? rsType.GetProperty("LocalPlayer", flags);

                if (playerProp != null)
                {
                    var playerType = playerProp.PropertyType;
                    LogWithTimestamp($"Found player property: {playerProp.Name} of type {playerType.Name}");

                    foreach (var prop in playerType.GetProperties(flags))
                    {
                        LogWithTimestamp($"  {playerProp.Name}.{prop.Name} : {prop.PropertyType.Name}");
                    }

                    _getGold ??= BuildChainedIntGetter(rsType, playerProp, playerType, "Gold", flags)
                              ?? BuildChainedIntGetter(rsType, playerProp, playerType, "CurrentGold", flags);
                    _getCurrentHp ??= BuildChainedIntGetter(rsType, playerProp, playerType, "CurrentHp", flags)
                                  ?? BuildChainedIntGetter(rsType, playerProp, playerType, "Hp", flags);
                    _getMaxHp ??= BuildChainedIntGetter(rsType, playerProp, playerType, "MaxHp", flags);
                }
            }

            LogWithTimestamp($"RunState reflection: Gold={(_getGold != null ? "found" : "MISSING")}, " +
                           $"CurrentHp={(_getCurrentHp != null ? "found" : "MISSING")}, " +
                           $"MaxHp={(_getMaxHp != null ? "found" : "MISSING")}");

            _runStateReflectionInitialized = true;
        }
        catch (Exception ex)
        {
            LogWithTimestamp($"Error initializing RunState reflection: {ex.Message}");
            _runStateReflectionInitialized = true; // Don't retry on failure
        }
    }

    /// <summary>
    /// Builds a Func that reads an int property from a RunState instance. Returns null if not found.
    /// </summary>
    static Func<RunState, int> BuildIntGetter(Type type, string propName, BindingFlags flags)
    {
        var prop = type.GetProperty(propName, flags);
        if (prop != null && (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(long)))
        {
            LogWithTimestamp($"Found {type.Name}.{propName}");
            return rs => (int)prop.GetValue(rs);
        }
        return null;
    }

    /// <summary>
    /// Builds a Func that reads an int property through an intermediate object (e.g., RunState.Player.Gold).
    /// </summary>
    static Func<RunState, int> BuildChainedIntGetter(Type rsType, PropertyInfo intermediateProp, Type intermediateType, string propName, BindingFlags flags)
    {
        var prop = intermediateType.GetProperty(propName, flags);
        if (prop != null && (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(long)))
        {
            LogWithTimestamp($"Found {intermediateProp.Name}.{propName}");
            return rs =>
            {
                var intermediate = intermediateProp.GetValue(rs);
                if (intermediate == null) return -1;
                return (int)prop.GetValue(intermediate);
            };
        }
        return null;
    }

    /// <summary>
    /// Gets the current gold from RunState via reflection. Returns -1 if unavailable.
    /// </summary>
    static int GetGold(RunState rs) => _getGold != null ? _getGold(rs) : -1;

    /// <summary>
    /// Gets the current HP from RunState via reflection. Returns -1 if unavailable.
    /// </summary>
    static int GetCurrentHp(RunState rs) => _getCurrentHp != null ? _getCurrentHp(rs) : -1;

    /// <summary>
    /// Gets the max HP from RunState via reflection. Returns -1 if unavailable.
    /// </summary>
    static int GetMaxHp(RunState rs) => _getMaxHp != null ? _getMaxHp(rs) : -1;

    /// <summary>
    /// Called when a new run starts. Stores the run state and calculates initial paths.
    /// </summary>
    static void OnRunStarted(RunState runState)
    {
        LogWithTimestamp("Run started");
        RouteSuggest.RunState = runState;
        InitializeRunStateReflection(runState);
        UpdateBestPath();
    }

    /// <summary>
    /// Called when entering a new act. Recalculates paths and requests map highlighting.
    /// </summary>
    static void OnActEntered()
    {
        LogWithTimestamp("Act entered");
        UpdateBestPath();
        RequestHighlightOnMapOpen();
    }

    /// <summary>
    /// Called when entering a room. Recalculates paths and requests map highlighting.
    /// </summary>
    static void OnRoomEntered()
    {
        LogWithTimestamp("Room entered");
        UpdateBestPath();
        RequestHighlightOnMapOpen();
    }

    /// <summary>
    /// Requests path highlighting. If map screen is already open, highlights immediately.
    /// Otherwise, subscribes to the Opened event to highlight when it opens.
    /// </summary>
    static void RequestHighlightOnMapOpen()
    {
        LogWithTimestamp("Requesting highlight when map opens");

        // Check if map screen is already open
        var mapScreen = NMapScreen.Instance;
        if (mapScreen != null && mapScreen.IsOpen)
        {
            LogWithTimestamp("Map screen is already open, highlighting immediately");
            HighlightBestPath();
            return;
        }

        _pendingHighlight = true;

        // Subscribe to map screen opened event
        if (mapScreen != null)
        {
            mapScreen.Opened += OnMapScreenOpened;
            LogWithTimestamp("Subscribed to map screen Opened event");
        }
        else
        {
            LogWithTimestamp("NMapScreen.Instance is null, will retry on next act/room");
        }
    }

    /// <summary>
    /// Convenience function that saves configuration, updates best path, and requests map highlight.
    /// Wraps the three common operations that are frequently called together.
    /// </summary>
    static void SaveAndUpdatePath()
    {
        SaveConfiguration();
        UpdateBestPath();
        RequestHighlightOnMapOpen();
    }

    /// <summary>
    /// Event handler for when the map screen opens. Applies pending highlights.
    /// </summary>
    static void OnMapScreenOpened()
    {
        LogWithTimestamp("Map screen opened event triggered");

        if (!_pendingHighlight)
        {
            LogWithTimestamp("No pending highlight, ignoring");
            return;
        }

        _pendingHighlight = false;

        // Unsubscribe from the event
        var mapScreen = NMapScreen.Instance;
        if (mapScreen != null)
        {
            mapScreen.Opened -= OnMapScreenOpened;
            LogWithTimestamp("Unsubscribed from map screen Opened event");
        }

        // Apply the highlight
        HighlightBestPath();
    }

    /// <summary>
    /// Called when exiting a room. Recalculates paths for the new position.
    /// </summary>
    static void OnRoomExited()
    {
        LogWithTimestamp("Room exited");
        UpdateBestPath();
    }

    /// <summary>
    /// Calculates optimal paths for all configured path types from current position.
    /// Uses CurrentMapPoint if available, otherwise falls back to StartingMapPoint.
    /// </summary>
    static void UpdateBestPath()
    {
        var runState = RouteSuggest.RunState;
        if (runState == null)
        {
            return;
        }

        LogWithTimestamp($"Current act index {runState.CurrentActIndex}");
        LogWithTimestamp($"Floor {runState.ActFloor}/{runState.TotalFloor}");

        // Get current game state for dynamic scoring
        int gold = GetGold(runState);
        int currentHp = GetCurrentHp(runState);
        int maxHp = GetMaxHp(runState);
        LogWithTimestamp($"Player state: Gold={gold}, HP={currentHp}/{maxHp}");

        // Get current position, fallback to starting point if not set
        var startPoint = runState.CurrentMapPoint ?? runState.Map?.StartingMapPoint;

        if (startPoint != null)
        {
            LogWithTimestamp($"At map point {startPoint.coord}");

            // Calculate paths for all configured path types
            CalculatedPaths.Clear();
            foreach (var config in PathConfigs)
            {
                var paths = FindOptimalPaths(startPoint, config, gold, currentHp, maxHp);
                if (paths.Count > 0)
                {
                    double score = config.CalculateScore(paths[0], gold, currentHp, maxHp);
                    LogWithTimestamp($"{config.Name}: {paths.Count} optimal path(s) found with score {score:F2}");
                    CalculatedPaths[config.Name] = paths;
                }
            }
        }
    }

    /// <summary>
    /// Finds optimal paths from startPoint to the Boss using the given configuration's scoring.
    /// Returns either one path (HighlightType.One) or all paths tied for best score (HighlightType.All).
    /// </summary>
    /// <param name="startPoint">Starting map point.</param>
    /// <param name="config">Path configuration with scoring weights.</param>
    /// <param name="gold">Current gold for dynamic scoring, or -1 if unavailable.</param>
    /// <param name="currentHp">Current HP for dynamic scoring, or -1 if unavailable.</param>
    /// <param name="maxHp">Max HP for dynamic scoring, or -1 if unavailable.</param>
    /// <returns>List of optimal paths. Empty list if no paths found.</returns>
    static List<List<MapPoint>> FindOptimalPaths(MapPoint startPoint, PathConfig config,
        int gold = -1, int currentHp = -1, int maxHp = -1)
    {
        if (startPoint == null) return new List<List<MapPoint>>();

        // DFS to find all paths to Boss
        var currentPath = new List<MapPoint>();
        var allPaths = new List<List<MapPoint>>();
        FindAllPathsToBoss(startPoint, currentPath, allPaths);

        // Sort paths by their coordinate sequence for reproducibility
        allPaths.Sort((a, b) =>
        {
            int minLen = Math.Min(a.Count, b.Count);
            for (int i = 0; i < minLen; i++)
            {
                int cmp = a[i].coord.CompareTo(b[i].coord);
                if (cmp != 0) return cmp;
            }
            return a.Count.CompareTo(b.Count);
        });

        LogWithTimestamp($"Found {allPaths.Count} path(s) to Boss");

        // Calculate scores for each path and find the best
        if (allPaths.Count == 0)
        {
            return new List<List<MapPoint>>();
        }

        double bestScore = double.MinValue;
        var optimalPaths = new List<List<MapPoint>>();

        for (int i = 0; i < allPaths.Count; i++)
        {
            double score = config.CalculateScore(allPaths[i], gold, currentHp, maxHp);
            LogWithTimestamp($"Path {i + 1} score: {score:F2}");

            if (score > bestScore)
            {
                bestScore = score;
                optimalPaths.Clear();
                optimalPaths.Add(allPaths[i]);
            }
            else if (Math.Abs(score - bestScore) < 0.001)
            {
                optimalPaths.Add(allPaths[i]);
            }
        }

        LogWithTimestamp($"Found {optimalPaths.Count} optimal path(s) with score {bestScore:F2}");

        // If HighlightType.One, return only one path (first for consistency)
        if (CurrentHighlightType == HighlightType.One && optimalPaths.Count > 1)
        {
            return new List<List<MapPoint>> { optimalPaths[0] };
        }

        return optimalPaths;
    }

    /// <summary>
    /// Recursive DFS helper to find all paths from current point to the Boss.
    /// </summary>
    /// <param name="current">Current map point.</param>
    /// <param name="currentPath">Accumulated path so far.</param>
    /// <param name="allPaths">Output list to collect all complete paths.</param>
    static void FindAllPathsToBoss(MapPoint current, List<MapPoint> currentPath, List<List<MapPoint>> allPaths)
    {
        if (current == null) return;

        // Add current point to path
        currentPath.Add(current);

        // Check if we reached the Boss
        if (current.PointType == MapPointType.Boss)
        {
            // Found a path to Boss, save a copy
            allPaths.Add(new List<MapPoint>(currentPath));
        }
        else if (current.Children != null && current.Children.Count > 0)
        {
            // Continue DFS on children
            foreach (var child in current.Children)
            {
                FindAllPathsToBoss(child, currentPath, allPaths);
            }
        }

        // Backtrack: remove current point from path
        currentPath.RemoveAt(currentPath.Count - 1);
    }

    /// <summary>
    /// Highlights the calculated paths on the map screen. Higher priority paths render on top.
    /// Uses reflection to access the map's internal path rendering data.
    /// </summary>
    static void HighlightBestPath()
    {
        if (!_reflectionInitialized)
        {
            LogWithTimestamp("Highlighting skipped - reflection not initialized");
            return;
        }
        if (CalculatedPaths.Count == 0)
        {
            LogWithTimestamp("Highlighting skipped - no paths calculated");
            return;
        }

        try
        {
            // Clear any previous highlighting first
            ClearPathHighlighting();

            // Get the map screen instance
            var mapScreen = NMapScreen.Instance;
            if (mapScreen == null)
            {
                LogWithTimestamp("Highlighting skipped - NMapScreen.Instance is null");
                return;
            }
            if (!mapScreen.IsOpen)
            {
                LogWithTimestamp("Highlighting skipped - Map screen is not open");
                return;
            }

            // Get the _paths dictionary using reflection
            var paths = _pathsField.GetValue(mapScreen) as System.Collections.IDictionary;
            if (paths == null)
            {
                LogWithTimestamp("Failed to get paths dictionary");
                return;
            }

            // Build segment sets for each path type
            var pathSegments = new Dictionary<string, HashSet<(MapCoord, MapCoord)>>();
            foreach (var kvp in CalculatedPaths)
            {
                var pathList = kvp.Value;
                if (pathList != null && pathList.Count > 0)
                {
                    var segments = new HashSet<(MapCoord, MapCoord)>();
                    foreach (var path in pathList)
                    {
                        if (path != null && path.Count >= 2)
                        {
                            for (int i = 0; i < path.Count - 1; i++)
                            {
                                segments.Add((path[i].coord, path[i + 1].coord));
                            }
                        }
                    }
                    if (segments.Count > 0)
                    {
                        pathSegments[kvp.Key] = segments;
                    }
                }
            }

            // Assign color to each segment based on priority (highest priority wins)
            var segmentColors = new Dictionary<(MapCoord, MapCoord), Color>();
            var sortedConfigs = PathConfigs.OrderByDescending(c => c.Priority).ToList();

            foreach (var config in sortedConfigs)
            {
                if (!pathSegments.TryGetValue(config.Name, out var segments))
                    continue;

                foreach (var segment in segments)
                {
                    // Normalize segment direction for deduplication
                    var normalizedKey = segment.Item1.CompareTo(segment.Item2) <= 0
                        ? segment
                        : (segment.Item2, segment.Item1);

                    // Assign color if not already assigned (higher priority paths win)
                    if (!segmentColors.ContainsKey(normalizedKey))
                    {
                        segmentColors[normalizedKey] = config.Color;
                    }
                }
            }

            // Apply highlighting to all unique segments
            foreach (var kvp in segmentColors)
            {
                var segment = kvp.Key;
                var color = kvp.Value;

                // Try both directions (from->to and to->from)
                var key1 = segment;
                var key2 = (segment.Item2, segment.Item1);

                object pathTicks = null;
                if (paths.Contains(key1))
                {
                    pathTicks = paths[key1];
                }
                else if (paths.Contains(key2))
                {
                    pathTicks = paths[key2];
                }

                if (pathTicks != null)
                {
                    var ticks = pathTicks as IReadOnlyList<TextureRect>;
                    if (ticks != null)
                    {
                        foreach (var tick in ticks)
                        {
                            if (tick != null && GodotObject.IsInstanceValid(tick))
                            {
                                // Save original color and scale if not already saved
                                if (!OriginalTickProperties.ContainsKey(tick))
                                {
                                    OriginalTickProperties[tick] = (tick.Modulate, tick.Scale);
                                }
                                tick.Modulate = color;
                                tick.Scale = new Vector2(1.4f, 1.4f);
                            }
                        }
                    }
                }
            }

            LogWithTimestamp($"Highlighted {segmentColors.Count} unique path segments");
        }
        catch (Exception ex)
        {
            LogWithTimestamp($"Error highlighting path: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears all path highlighting by restoring original colors and scales.
    /// Also cleans up invalid TextureRect entries from the cache.
    /// </summary>
    static void ClearPathHighlighting()
    {
        try
        {
            // Reset highlighted path ticks to their original color and scale
            var ticksToRemove = new List<TextureRect>();
            foreach (var kvp in OriginalTickProperties)
            {
                var tick = kvp.Key;
                var (originalColor, originalScale) = kvp.Value;

                if (tick != null && GodotObject.IsInstanceValid(tick))
                {
                    tick.Modulate = originalColor;
                    tick.Scale = originalScale;
                }
                else
                {
                    // Mark invalid ticks for removal
                    ticksToRemove.Add(tick);
                }
            }

            // Clean up invalid entries
            foreach (var tick in ticksToRemove)
            {
                OriginalTickProperties.Remove(tick);
            }
        }
        catch (Exception ex)
        {
            LogWithTimestamp($"Error clearing path highlighting: {ex.Message}");
        }
    }
}
