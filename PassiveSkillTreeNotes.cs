using System.Collections;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Interfaces;
using ImGuiNET;

namespace PassiveSkillTreeNotes;

public sealed class PassiveSkillTreeNotes
    : BaseSettingsPlugin<PassiveSkillTreeNotesSettings>
{
    private const long RefreshIntervalMilliseconds = 250;
    private const string PlanterInternalName = "PassiveSkillTreePlanter";
    private static readonly InventorySlotE[] EquippedInventorySlots =
    [
        InventorySlotE.Weapon1,
        InventorySlotE.Offhand1,
        InventorySlotE.Weapon2,
        InventorySlotE.Offhand2,
        InventorySlotE.Helm1,
        InventorySlotE.BodyArmour1,
        InventorySlotE.Gloves1,
        InventorySlotE.Boots1,
        InventorySlotE.Amulet1,
        InventorySlotE.Ring1,
        InventorySlotE.Ring2,
        InventorySlotE.Ring3,
        InventorySlotE.Belt1
    ];
    private static readonly Dictionary<string, string[]> GemNameAliases =
        new(StringComparer.Ordinal)
        {
            ["concentrated effect"] = ["conc effect"]
        };
    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private IPlugin? _planter;
    private PropertyInfo? _selectedBuildProperty;
    private PropertyInfo? _lastSelectedCharacterUrlProperty;
    private PropertyInfo? _buildDirectoryProperty;
    private FieldInfo? _importersField;
    private NotesCache _cache = new();
    private string _selectedBuild = string.Empty;
    private string _activeTreeUrl = string.Empty;
    private string _activeSourceUrl = string.Empty;
    private string _sourceUrlInput = string.Empty;
    private string _status = "Waiting for PassiveSkillTreePlanter.";
    private string _fetchStatus = string.Empty;
    private long _nextRefreshAt;
    private string? _observedBuildPath;
    private DateTime _observedBuildWriteTimeUtc;
    private bool _resetTreeScroll;
    private bool _resetLeagueStartScroll;
    private string _normalizedNotes = string.Empty;
    private readonly HashSet<string> _ownedGemMetadata =
        new(StringComparer.OrdinalIgnoreCase);
    private long _nextOwnedGemRefreshAt;
    private Task<FetchResult>? _fetchTask;
    private IReadOnlyList<ColoredNotesRenderer.StyledCharacter> _notes = [];

    private string CachePath => Path.Combine(ConfigDirectory, "build-notes.json");

    public override bool Initialise()
    {
        Name = "Passive Skill Tree Notes";
        Description = "Displays PoB notes matching the tree loaded by PassiveSkillTreePlanter.";
        LoadCache();
        return true;
    }

    public override void Render()
    {
        if (!Settings.Enable.Value)
        {
            return;
        }

        var ingameUi = GameController.IngameState?.IngameUi;
        var showTreeNotes = ingameUi?.TreePanel?.IsVisible == true;
        var showLeagueStartNotes = Settings.LeagueStartMode.Value &&
                                   (ingameUi?.PurchaseWindow?.IsVisible == true ||
                                    ingameUi?.QuestRewardWindow?.IsVisible == true);
        if (!showTreeNotes && !showLeagueStartNotes)
        {
            return;
        }

        RefreshPlanterStateIfDue();
        CompleteFetchIfReady();

        if (showTreeNotes)
        {
            DrawNotesWindow("PassiveSkillTreeNotes", ref _resetTreeScroll);
        }

        if (showLeagueStartNotes)
        {
            DrawNotesWindow(
                "PassiveSkillTreeNotesLeagueStart",
                ref _resetLeagueStartScroll);
            DrawMentionedGemFrames();
        }
    }

    private void DrawNotesWindow(string windowId, ref bool resetScroll)
    {
        ImGui.SetNextWindowPos(new Vector2(280, 20), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(560, 720), ImGuiCond.FirstUseEver);

        var visibleTitle = string.IsNullOrWhiteSpace(_selectedBuild)
            ? "PoB Notes"
            : $"PoB Notes — {_selectedBuild}";

        if (!ImGui.Begin($"{visibleTitle}###{windowId}"))
        {
            ImGui.End();
            return;
        }

        DrawSourceEditor();

        if (_notes.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(_status))
            {
                ImGui.TextWrapped(_status);
            }

            ImGui.End();
            return;
        }

        if (ImGui.BeginChild(
                $"##{windowId}Scroll",
                Vector2.Zero,
                ImGuiChildFlags.None,
                ImGuiWindowFlags.None))
        {
            if (resetScroll)
            {
                ImGui.SetScrollY(0);
                resetScroll = false;
            }

            ColoredNotesRenderer.Draw(_notes);
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawSourceEditor()
    {
        if (string.IsNullOrWhiteSpace(_selectedBuild))
        {
            return;
        }

        var sourceCount = GetAssociatedSourceUrls(_selectedBuild).Count;
        var headerLabel = sourceCount > 1 ? $"PoB sources ({sourceCount})" : "PoB source";
        var flags = _notes.Count == 0 || _fetchTask != null
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;

        if (!ImGui.CollapsingHeader(headerLabel, flags))
        {
            return;
        }

        var buttonWidth = 98f;
        ImGui.SetNextItemWidth(Math.Max(100f, ImGui.GetContentRegionAvail().X - buttonWidth - 8f));
        ImGui.InputTextWithHint(
            "##PassiveSkillTreeNotesSource",
            "https://pobb.in/...",
            ref _sourceUrlInput,
            300);
        ImGui.SameLine();

        var canFetch = _fetchTask == null && TryNormalizePobbUrl(_sourceUrlInput, out _, out _);
        ImGui.BeginDisabled(!canFetch);
        if (ImGui.Button(
                string.IsNullOrWhiteSpace(_activeSourceUrl) ? "Fetch notes" : "Refresh",
                new Vector2(buttonWidth, 0)))
        {
            StartFetch(_selectedBuild, _sourceUrlInput, "Fetching notes...");
        }

        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(_fetchStatus))
        {
            ImGui.TextWrapped(_fetchStatus);
        }

        ImGui.Separator();
    }

    private void DrawMentionedGemFrames()
    {
        if (string.IsNullOrWhiteSpace(_normalizedNotes))
        {
            return;
        }

        RefreshOwnedGemsIfDue();

        var ingameUi = GameController.IngameState?.IngameUi;
        var purchaseWindow = ingameUi?.PurchaseWindow;
        if (purchaseWindow?.IsVisible == true)
        {
            var vendorItems = purchaseWindow.TabContainer?
                .VisibleStash?
                .VisibleInventoryItems;
            if (vendorItems != null)
            {
                foreach (var vendorItem in vendorItems)
                {
                    DrawGemFrameIfMentioned(
                        vendorItem?.Item,
                        vendorItem,
                        Settings.ShowVendorGemNames.Value);
                }
            }
        }

        var rewardWindow = ingameUi?.QuestRewardWindow;
        if (rewardWindow?.IsVisible != true)
        {
            return;
        }

        var rewards = rewardWindow.GetPossibleRewards();
        if (rewards == null)
        {
            return;
        }

        foreach (var (entity, element) in rewards)
        {
            DrawGemFrameIfMentioned(
                entity,
                element,
                Settings.ShowRewardGemNames.Value);
        }
    }

    private void DrawGemFrameIfMentioned(
        Entity? entity,
        Element? element,
        bool showName)
    {
        var metadata = entity?.Path;
        if (entity?.IsValid != true ||
            element?.IsVisible != true ||
            string.IsNullOrWhiteSpace(metadata) ||
            !metadata.StartsWith("Metadata/Items/Gems/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Settings.HideOwnedGems.Value && _ownedGemMetadata.Contains(metadata))
        {
            return;
        }

        var baseName = GameController.Files.BaseItemTypes
            .Translate(metadata)?
            .BaseName;
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return;
        }

        var normalizedName = NormalizeForMatch(baseName);
        const string supportSuffix = " support";
        if (normalizedName.EndsWith(supportSuffix, StringComparison.Ordinal))
        {
            normalizedName = normalizedName[..^supportSuffix.Length];
        }

        if (string.IsNullOrWhiteSpace(normalizedName) ||
            !IsGemMentioned(normalizedName))
        {
            return;
        }

        var itemRect = element.GetClientRectCache;
        if (itemRect.Width <= 0 || itemRect.Height <= 0)
        {
            return;
        }

        Graphics.DrawFrame(
            itemRect,
            Settings.GemBorderColor.Value,
            Settings.GemBorderThickness.Value);

        if (showName)
        {
            DrawGemName(baseName, itemRect);
        }
    }

    private void DrawGemName(string baseName, SharpDX.RectangleF itemRect)
    {
        var textSize = Graphics.MeasureText(baseName);
        var padding = new Vector2(4, 2);
        var backgroundSize = textSize + padding * 2;
        var backgroundPosition = new Vector2(
            itemRect.Left + (itemRect.Width - backgroundSize.X) / 2,
            itemRect.Bottom - backgroundSize.Y);

        Graphics.DrawBox(
            backgroundPosition,
            backgroundPosition + backgroundSize,
            SharpDX.Color.Black);
        Graphics.DrawText(
            baseName,
            backgroundPosition + padding,
            SharpDX.Color.White);
    }

    private void RefreshOwnedGemsIfDue()
    {
        var now = Environment.TickCount64;
        if (now < _nextOwnedGemRefreshAt)
        {
            return;
        }

        _nextOwnedGemRefreshAt = now + RefreshIntervalMilliseconds;
        _ownedGemMetadata.Clear();

        if (!Settings.HideOwnedGems.Value)
        {
            return;
        }

        foreach (var slot in EquippedInventorySlots)
        {
            var inventory = TryGetPlayerInventory(slot);
            if (inventory?.InventorySlotItems == null)
            {
                continue;
            }

            foreach (var inventoryItem in inventory.InventorySlotItems)
            {
                var socketedGems = inventoryItem?
                    .Item?
                    .GetComponent<Sockets>()?
                    .SocketedGems;
                if (socketedGems == null)
                {
                    continue;
                }

                foreach (var socketedGem in socketedGems)
                {
                    AddOwnedGem(socketedGem?.GemEntity);
                }
            }
        }

        AddInventoryGems(InventorySlotE.MainInventory1);
        AddInventoryGems(InventorySlotE.ExpandedMainInventory1);
    }

    private void AddInventoryGems(InventorySlotE slot)
    {
        var inventory = TryGetPlayerInventory(slot);
        if (inventory?.InventorySlotItems == null)
        {
            return;
        }

        foreach (var inventoryItem in inventory.InventorySlotItems)
        {
            AddOwnedGem(inventoryItem?.Item);
        }
    }

    private ServerInventory? TryGetPlayerInventory(InventorySlotE slot)
    {
        try
        {
            return GameController.IngameState?
                .ServerData?
                .GetPlayerInventoryBySlot(slot);
        }
        catch
        {
            return null;
        }
    }

    private void AddOwnedGem(Entity? entity)
    {
        var metadata = entity?.Path;
        if (!string.IsNullOrWhiteSpace(metadata) &&
            metadata.StartsWith(
                "Metadata/Items/Gems/",
                StringComparison.OrdinalIgnoreCase))
        {
            _ownedGemMetadata.Add(metadata);
        }
    }

    private bool IsGemMentioned(string normalizedName)
    {
        if (_normalizedNotes.Contains($" {normalizedName} ", StringComparison.Ordinal))
        {
            return true;
        }

        return GemNameAliases.TryGetValue(normalizedName, out var aliases) &&
               aliases.Any(alias =>
                   _normalizedNotes.Contains($" {alias} ", StringComparison.Ordinal));
    }

    private void RefreshPlanterStateIfDue()
    {
        var now = Environment.TickCount64;
        if (now < _nextRefreshAt)
        {
            return;
        }

        _nextRefreshAt = now + RefreshIntervalMilliseconds;

        if (!TryResolvePlanter())
        {
            SelectBuild(string.Empty, "PassiveSkillTreePlanter is not loaded.");
            return;
        }

        var settings = _planter!._Settings;
        var selectedBuild = _selectedBuildProperty?.GetValue(settings) as string;
        if (string.IsNullOrWhiteSpace(selectedBuild))
        {
            SelectBuild(string.Empty, "PassiveSkillTreePlanter has no selected build.");
            return;
        }

        if (!string.Equals(_selectedBuild, selectedBuild, StringComparison.Ordinal))
        {
            SelectBuild(selectedBuild, string.Empty);
        }

        ObserveSelectedBuildFile(selectedBuild);

        var activeTreeUrl = _lastSelectedCharacterUrlProperty?.GetValue(settings) as string
                            ?? string.Empty;
        if (!string.Equals(_activeTreeUrl, activeTreeUrl, StringComparison.Ordinal))
        {
            _activeTreeUrl = activeTreeUrl;
            UpdateDisplayedNotes();
        }
    }

    private bool TryResolvePlanter()
    {
        if (_planter != null &&
            PluginManager.Plugins.Any(wrapper => ReferenceEquals(wrapper.Plugin, _planter)))
        {
            return true;
        }

        _planter = PluginManager.Plugins
            .Select(wrapper => wrapper.Plugin)
            .FirstOrDefault(plugin =>
                string.Equals(
                    plugin.InternalName,
                    PlanterInternalName,
                    StringComparison.OrdinalIgnoreCase));

        var settingsType = _planter?._Settings.GetType();
        _selectedBuildProperty = settingsType?
            .GetProperty("SelectedBuild", BindingFlags.Instance | BindingFlags.Public);
        _lastSelectedCharacterUrlProperty = settingsType?
            .GetProperty("LastSelectedCharacterUrl", BindingFlags.Instance | BindingFlags.Public);
        _buildDirectoryProperty = _planter?
            .GetType()
            .GetProperty("SkillTreeUrlFilesDir", BindingFlags.Instance | BindingFlags.Public);
        _importersField = _planter?
            .GetType()
            .GetField("_importers", BindingFlags.Instance | BindingFlags.NonPublic);

        return _planter != null &&
               _selectedBuildProperty != null &&
               _lastSelectedCharacterUrlProperty != null &&
               _buildDirectoryProperty != null &&
               _importersField != null;
    }

    private void SelectBuild(string buildName, string unavailableStatus)
    {
        _selectedBuild = buildName;
        _activeTreeUrl = string.Empty;
        _activeSourceUrl = string.Empty;
        _fetchStatus = string.Empty;
        ResetWindowScrolls();
        _observedBuildPath = null;
        _observedBuildWriteTimeUtc = default;

        if (string.IsNullOrWhiteSpace(buildName))
        {
            _sourceUrlInput = string.Empty;
            _notes = [];
            _normalizedNotes = string.Empty;
            _status = unavailableStatus;
            return;
        }

        var sourceUrls = GetAssociatedSourceUrls(buildName);
        _sourceUrlInput = sourceUrls.LastOrDefault() ?? string.Empty;
        _notes = [];
        _normalizedNotes = string.Empty;
        _status = sourceUrls.Count == 0
            ? $"No PoB source has been linked to \"{buildName}\" yet."
            : "Load one of this build's imported passive trees to select its notes.";
    }

    private void UpdateDisplayedNotes()
    {
        var fingerprint = PassiveTreeFingerprint.FromUrl(_activeTreeUrl);
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            _activeSourceUrl = string.Empty;
            _notes = [];
            _normalizedNotes = string.Empty;
            _status = "No character passive tree is currently loaded.";
            ResetWindowScrolls();
            return;
        }

        var matchedSource = FindMatchingSource(_selectedBuild, fingerprint);
        if (matchedSource == null)
        {
            _activeSourceUrl = string.Empty;
            _notes = [];
            _normalizedNotes = string.Empty;
            _status = GetAssociatedSourceUrls(_selectedBuild).Count == 0
                ? $"No PoB source has been linked to \"{_selectedBuild}\" yet."
                : "The loaded passive tree does not match any cached PoB source for this build.";
            ResetWindowScrolls();
            return;
        }

        _activeSourceUrl = matchedSource.PobbUrl;
        _sourceUrlInput = matchedSource.PobbUrl;
        SetNotes(matchedSource.Notes);
    }

    private CachedPobSource? FindMatchingSource(string buildName, string fingerprint)
    {
        var associatedUrls = GetAssociatedSourceUrls(buildName);
        for (var index = associatedUrls.Count - 1; index >= 0; index--)
        {
            if (_cache.Sources.TryGetValue(associatedUrls[index], out var source) &&
                source.TreeFingerprints.Contains(fingerprint, StringComparer.Ordinal))
            {
                return source;
            }
        }

        return _cache.Sources.Values.FirstOrDefault(source =>
            source.TreeFingerprints.Contains(fingerprint, StringComparer.Ordinal));
    }

    private void ObserveSelectedBuildFile(string selectedBuild)
    {
        var buildDirectory = _buildDirectoryProperty?.GetValue(_planter) as string;
        if (string.IsNullOrWhiteSpace(buildDirectory))
        {
            return;
        }

        var buildPath = Path.Combine(buildDirectory, selectedBuild + ".json");
        if (!File.Exists(buildPath))
        {
            _observedBuildPath = null;
            _observedBuildWriteTimeUtc = default;
            return;
        }

        var writeTimeUtc = File.GetLastWriteTimeUtc(buildPath);
        if (!string.Equals(_observedBuildPath, buildPath, StringComparison.OrdinalIgnoreCase))
        {
            _observedBuildPath = buildPath;
            _observedBuildWriteTimeUtc = writeTimeUtc;
            return;
        }

        if (writeTimeUtc == _observedBuildWriteTimeUtc)
        {
            return;
        }

        var importerUrl = GetLivePobbImporterUrl();
        if (TryNormalizePobbUrl(importerUrl, out var normalizedUrl, out _))
        {
            if (_cache.Sources.ContainsKey(normalizedUrl))
            {
                AssociateSource(selectedBuild, normalizedUrl);
                SaveCacheSafely();
                UpdateDisplayedNotes();
            }
            else if (!StartFetch(
                         selectedBuild,
                         normalizedUrl,
                         $"Detected a new PoB import for \"{selectedBuild}\". Fetching notes..."))
            {
                return;
            }
        }

        _observedBuildWriteTimeUtc = writeTimeUtc;
    }

    private string GetLivePobbImporterUrl()
    {
        if (_planter == null ||
            _importersField?.GetValue(_planter) is not IEnumerable importers)
        {
            return string.Empty;
        }

        foreach (var importer in importers)
        {
            if (importer == null ||
                !string.Equals(
                    importer.GetType().Name,
                    "PobbinTreeImporter",
                    StringComparison.Ordinal))
            {
                continue;
            }

            return FindField(importer.GetType(), "_urlInput")?.GetValue(importer) as string
                   ?? string.Empty;
        }

        return string.Empty;
    }

    private static FieldInfo? FindField(Type type, string fieldName)
    {
        for (Type? currentType = type; currentType != null; currentType = currentType.BaseType)
        {
            var field = currentType.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                return field;
            }
        }

        return null;
    }

    private bool StartFetch(string buildName, string sourceUrl, string status)
    {
        if (_fetchTask != null ||
            string.IsNullOrWhiteSpace(buildName) ||
            !TryNormalizePobbUrl(sourceUrl, out var normalizedUrl, out var pobbId))
        {
            return false;
        }

        if (string.Equals(_selectedBuild, buildName, StringComparison.Ordinal))
        {
            _sourceUrlInput = normalizedUrl;
        }

        _fetchStatus = status;
        _fetchTask = FetchNotesAsync(buildName, normalizedUrl, pobbId);
        return true;
    }

    private void CompleteFetchIfReady()
    {
        if (_fetchTask == null || !_fetchTask.IsCompleted)
        {
            return;
        }

        FetchResult result;
        try
        {
            result = _fetchTask.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _fetchStatus = $"Could not fetch notes: {exception.Message}";
            _fetchTask = null;
            return;
        }

        _fetchTask = null;

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            _fetchStatus = result.Error;
            return;
        }

        _cache.Sources[result.PobbUrl] = new CachedPobSource
        {
            PobbUrl = result.PobbUrl,
            Notes = result.Notes,
            TreeFingerprints = result.TreeFingerprints
        };
        AssociateSource(result.BuildName, result.PobbUrl);
        SaveCacheSafely();

        if (string.Equals(_selectedBuild, result.BuildName, StringComparison.Ordinal))
        {
            _sourceUrlInput = result.PobbUrl;
            UpdateDisplayedNotes();
            _fetchStatus = string.Equals(_activeSourceUrl, result.PobbUrl, StringComparison.Ordinal)
                ? "Notes loaded."
                : "PoB source cached. Load one of its imported trees to display its notes.";
        }
    }

    private void AssociateSource(string buildName, string sourceUrl)
    {
        if (!_cache.BuildSources.TryGetValue(buildName, out var sourceUrls))
        {
            sourceUrls = [];
            _cache.BuildSources[buildName] = sourceUrls;
        }

        sourceUrls.RemoveAll(url => string.Equals(url, sourceUrl, StringComparison.Ordinal));
        sourceUrls.Add(sourceUrl);
    }

    private List<string> GetAssociatedSourceUrls(string buildName)
    {
        return _cache.BuildSources.TryGetValue(buildName, out var sourceUrls)
            ? sourceUrls
            : [];
    }

    private void SetNotes(string notes)
    {
        var trimmedNotes = notes.Trim();
        _notes = ColoredNotesRenderer.Parse(trimmedNotes);
        var plainNotes = string.Concat(_notes.Select(character => character.Value));
        _normalizedNotes = $" {NormalizeForMatch(plainNotes)} ";
        _status = string.IsNullOrWhiteSpace(trimmedNotes)
            ? "The matched PoB does not contain notes."
            : string.Empty;
        ResetWindowScrolls();
    }

    private static string NormalizeForMatch(string value)
    {
        var normalized = new StringBuilder(value.Length);
        var separatorPending = false;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }

                normalized.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }

        return normalized.ToString();
    }

    private void ResetWindowScrolls()
    {
        _resetTreeScroll = true;
        _resetLeagueStartScroll = true;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<NotesCache>(
                File.ReadAllText(CachePath),
                JsonOptions);
            if (loaded != null)
            {
                _cache = loaded;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _status = $"Could not load the notes cache: {exception.Message}";
        }
    }

    private void SaveCacheSafely()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache, JsonOptions));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _fetchStatus = $"The notes cache could not be saved: {exception.Message}";
        }
    }

    private static async Task<FetchResult> FetchNotesAsync(
        string buildName,
        string pobbUrl,
        string pobbId)
    {
        try
        {
            var code = (await HttpClient.GetStringAsync($"https://pobb.in/{pobbId}/raw")).Trim();
            var xml = DecodePobCode(code);

            var document = new XmlDocument
            {
                XmlResolver = null
            };

            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
            document.Load(reader);

            var notes = document.DocumentElement?
                .SelectSingleNode("Notes")?
                .InnerText
                .Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(notes))
            {
                return FetchResult.Failed(
                    buildName,
                    pobbUrl,
                    "The linked PoB does not contain notes.");
            }

            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            var treeNode = document.DocumentElement?.SelectSingleNode("Tree");
            if (treeNode != null)
            {
                foreach (XmlNode spec in treeNode.ChildNodes)
                {
                    if (!string.Equals(spec.Name, "Spec", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var treeUrl = spec.ChildNodes
                        .Cast<XmlNode>()
                        .FirstOrDefault(node => string.Equals(node.Name, "URL", StringComparison.Ordinal))
                        ?.InnerText
                        .Trim();
                    var fingerprint = PassiveTreeFingerprint.FromUrl(treeUrl);
                    if (!string.IsNullOrWhiteSpace(fingerprint))
                    {
                        fingerprints.Add(fingerprint);
                    }
                }
            }

            if (fingerprints.Count == 0)
            {
                return FetchResult.Failed(
                    buildName,
                    pobbUrl,
                    "The linked PoB does not contain decodable passive trees.");
            }

            return new FetchResult(
                buildName,
                pobbUrl,
                notes,
                fingerprints.ToList(),
                string.Empty);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                TaskCanceledException or
                FormatException or
                InvalidDataException or
                XmlException)
        {
            return FetchResult.Failed(
                buildName,
                pobbUrl,
                $"Could not fetch notes: {exception.Message}");
        }
    }

    private static string DecodePobCode(string code)
    {
        var normalized = code.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };

        var compressed = Convert.FromBase64String(normalized);
        if (compressed.Length < 3)
        {
            throw new InvalidDataException("The PoB payload is too short.");
        }

        using var input = new MemoryStream(compressed);
        input.Seek(2, SeekOrigin.Begin);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool TryNormalizePobbUrl(
        string value,
        out string normalizedUrl,
        out string pobbId)
    {
        normalizedUrl = string.Empty;
        pobbId = string.Empty;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "pobb.in", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 1)
        {
            return false;
        }

        pobbId = segments[0];
        normalizedUrl = $"https://pobb.in/{pobbId}";
        return true;
    }

    private sealed class NotesCache
    {
        public Dictionary<string, CachedPobSource> Sources { get; set; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> BuildSources { get; set; } =
            new(StringComparer.Ordinal);
    }

    private sealed class CachedPobSource
    {
        public string PobbUrl { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;

        public List<string> TreeFingerprints { get; set; } = [];
    }

    private sealed record FetchResult(
        string BuildName,
        string PobbUrl,
        string Notes,
        List<string> TreeFingerprints,
        string Error)
    {
        public static FetchResult Failed(string buildName, string pobbUrl, string error)
        {
            return new FetchResult(buildName, pobbUrl, string.Empty, [], error);
        }
    }
}
