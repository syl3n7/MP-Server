namespace MP.Server.Inventory;

public sealed class CommandValidator
{
    public record ValidationResult(bool Ok, string ErrorCode = "", int SlotId = -1);

    public ValidationResult ValidateMoveSlot(Inventory inv, string sessionId, int from, int to)
    {
        if (inv.OwnerId != sessionId)    return new(false, "NOT_OWNER");
        if (inv.GetSlot(from) == null)   return new(false, "INVALID_SLOT", from);
        if (inv.GetSlot(to)   == null)   return new(false, "INVALID_SLOT", to);
        if (inv.GetSlot(from)!.IsEmpty)  return new(false, "SLOT_EMPTY",   from);

        var src  = inv.GetSlot(from)!;
        var dest = inv.GetSlot(to)!;

        // If same item, check stack capacity before attempting merge
        if (!dest.IsEmpty && dest.ItemId == src.ItemId)
        {
            var def = ItemRegistry.Instance.Get(src.ItemId);
            if (def != null && dest.Quantity >= def.MaxStack)
                return new(false, "STACK_FULL", to);
        }

        return new(true);
    }

    public ValidationResult ValidateDropItem(Inventory inv, string sessionId, int slotId)
    {
        if (inv.OwnerId != sessionId) return new(false, "NOT_OWNER");
        var slot = inv.GetSlot(slotId);
        if (slot == null)             return new(false, "INVALID_SLOT", slotId);
        if (slot.IsEmpty)             return new(false, "SLOT_EMPTY",   slotId);

        var def = ItemRegistry.Instance.Get(slot.ItemId);
        if (def != null && !def.IsDroppable) return new(false, "ITEM_NOT_DROPPABLE", slotId);

        return new(true);
    }

    public ValidationResult ValidateUseItem(Inventory inv, string sessionId, int slotId)
    {
        if (inv.OwnerId != sessionId) return new(false, "NOT_OWNER");
        var slot = inv.GetSlot(slotId);
        if (slot == null)             return new(false, "INVALID_SLOT", slotId);
        if (slot.IsEmpty)             return new(false, "SLOT_EMPTY",   slotId);

        var def = ItemRegistry.Instance.Get(slot.ItemId);
        if (def != null && !def.IsUsable) return new(false, "ITEM_NOT_USABLE", slotId);

        return new(true);
    }
}
