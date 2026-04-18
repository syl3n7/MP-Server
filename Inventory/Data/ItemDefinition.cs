using System.Collections.Generic;

namespace MP.Server.Inventory;

// Static item definition — loaded from items.json at startup, never sent over the wire.
public class ItemDefinition
{
    public string ItemId      { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int    MaxStack    { get; set; } = 99;
    public float  Weight      { get; set; } = 1f;
    public bool   IsDroppable { get; set; } = true;
    public bool   IsUsable    { get; set; } = false;
    public Dictionary<string, object> Tags { get; set; } = new();
}
