namespace QueryTranslation.Dtos;

// What a product listing screen actually renders: four columns out of eleven.
public class ProductListItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
}

// Same idea but reaching across the navigation, so EF has to emit a JOIN.
public class ProductWithCategoryDto
{
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string CategoryName { get; set; } = string.Empty;
}
