using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MP.Server.Inventory;

public sealed class ItemRegistry
{
    public static ItemRegistry Instance { get; } = new();

    private readonly Dictionary<string, ItemDefinition> _items = new();

    public void LoadFromJson(string path, ILogger? logger = null)
    {
        if (!File.Exists(path))
        {
            logger?.LogWarning("⚠️ items.json not found at {Path}. Inventory item definitions will be empty.", path);
            return;
        }

        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<ItemDefinition>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (list == null) return;

        foreach (var item in list)
            _items[item.ItemId] = item;

        logger?.LogInformation("📦 ItemRegistry loaded {Count} item definition(s)", _items.Count);
    }

    public ItemDefinition? Get(string itemId) =>
        _items.TryGetValue(itemId, out var def) ? def : null;

    public bool Exists(string itemId) => _items.ContainsKey(itemId);
}
