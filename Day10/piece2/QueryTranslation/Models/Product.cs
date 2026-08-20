namespace QueryTranslation.Models;

// Deliberately wide. Description and InternalNotes are the columns a product listing
// screen never needs, and they are what makes the difference between a whole-entity
// query and a projection measurable rather than academic.
public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public double Weight { get; set; }
    public bool IsDiscontinued { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Description { get; set; } = string.Empty;
    public string InternalNotes { get; set; } = string.Empty;

    public int CategoryId { get; set; }
    public Category? Category { get; set; }
}
