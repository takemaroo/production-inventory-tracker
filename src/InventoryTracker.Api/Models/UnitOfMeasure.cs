namespace InventoryTracker.Api.Models
{
    public class UnitOfMeasure
    {
        public int UnitOfMeasureId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Abbreviation { get; set; } = string.Empty;
    }
}