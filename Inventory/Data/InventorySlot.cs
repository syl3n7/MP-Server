using System.Collections.Generic;

namespace MP.Server.Inventory;

public class InventorySlot
{
    public int    SlotId   { get; set; }
    public string ItemId   { get; set; } = "";
    public int    Quantity { get; set; } = 0;
    public Dictionary<string, object> Metadata { get; set; } = new();

    public bool IsEmpty => string.IsNullOrEmpty(ItemId) || Quantity <= 0;

    public Dictionary<string, object> ToDict() => new()
    {
        ["slotId"]   = SlotId,
        ["itemId"]   = ItemId,
        ["quantity"] = Quantity,
        ["meta"]     = Metadata
    };
}
