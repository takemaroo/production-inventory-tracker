namespace InventoryTracker.Api.Models
{
    public class Item
    {
        public int ItemId { get; set; }
        public int UnitOfMeasureId { get; set; }
        public UnitOfMeasure? UnitOfMeasure { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public ItemType Type { get; set; }
    }

}