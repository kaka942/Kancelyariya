namespace Kancelyariya.Models;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Unit { get; set; } = "шт.";
    public int Qty { get; set; }
    public int MinQty { get; set; }

    public bool IsLow => Qty <= MinQty;
    public string StatusText => IsLow ? "⚠ Пора заказать" : "В норме";
    public int DeficitQty => Math.Max(0, MinQty - Qty);
    public int RecommendedOrderQty => Math.Max(1, (MinQty * 2) - Qty);
}
