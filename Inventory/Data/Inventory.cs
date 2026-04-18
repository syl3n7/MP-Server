using System.Collections.Generic;
using System.Linq;

namespace MP.Server.Inventory;

public class Inventory
{
    public string OwnerId  { get; set; }
    public int    MaxSlots { get; set; } = 30;
    public InventorySlot[] Slots { get; set; }

    public Inventory(string ownerId, int maxSlots = 30)
    {
        OwnerId  = ownerId;
        MaxSlots = maxSlots;
        Slots    = Enumerable.Range(0, maxSlots)
                             .Select(i => new InventorySlot { SlotId = i })
                             .ToArray();
    }

    public InventorySlot? GetSlot(int slotId) =>
        slotId >= 0 && slotId < Slots.Length ? Slots[slotId] : null;

    public List<Dictionary<string, object>> Serialise() =>
        Slots.Select(s => s.ToDict()).ToList();
}
