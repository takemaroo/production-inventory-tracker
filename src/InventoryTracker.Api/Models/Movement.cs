namespace InventoryTracker.Api.Models
{
    public class Movement
    {
        public int MovementId { get; set; }
        public int ItemId { get; set; }
        public Item? Item { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public decimal Quantity { get; set; } = 0;
        public MovementType Type { get; set; }
    }
}