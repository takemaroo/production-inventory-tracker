using System.Globalization;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Tests.Unit.Calculations;

/// <summary>
/// Builds the in-memory orders and lines the calculation tests need. Nothing is saved; the calculations are
/// pure functions, so plain objects are enough.
/// </summary>
internal static class CalculationTestData
{
    /// <summary>Raw material id used for Cheese in the PRD examples (matches design 5.7).</summary>
    public const int Cheese = 3;

    /// <summary>Raw material id used for Potato starch in the design 5.7 example.</summary>
    public const int PotatoStarch = 4;

    /// <summary>The PRD default tolerance, 2% (D-1).</summary>
    public const decimal DefaultTolerance = 2m;

    /// <summary>A UTC time on the PRD example day, 2026-11-11, at the given hour and minute.</summary>
    public static DateTime At(int hour, int minute = 0) => new(2026, 11, 11, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>A production order with one line per (material, planned amount) pair.</summary>
    public static ProductionOrder Order(
        int number, DateTime createdAt, OrderStatus status, params (int RawMaterialId, decimal Planned)[] lines)
    {
        var order = new ProductionOrder { Number = number, CreatedAt = createdAt, Status = status };
        var lineNo = 1;
        foreach (var (rawMaterialId, planned) in lines)
        {
            order.Lines.Add(new ProductionOrderLine
            {
                OrderNumber = number,
                LineNo = lineNo++,
                RawMaterialId = rawMaterialId,
                AmountPerUnit = 1m,
                PlannedAmount = planned,
            });
        }
        return order;
    }

    /// <summary>A line as it stands after the send: the figures D-9 reads at completion are filled in.</summary>
    public static ProductionOrderLine SentLine(
        decimal amountPerUnit, decimal leftoverApplied, decimal warehouseSend, decimal complementarySends, decimal carriedLeftover)
        => new()
        {
            RawMaterialId = Cheese,
            AmountPerUnit = amountPerUnit,
            LeftoverApplied = leftoverApplied,
            WarehouseSend = warehouseSend,
            ComplementarySends = complementarySends,
            CarriedLeftover = carriedLeftover,
        };

    /// <summary>
    /// Parses a decimal written as text. xUnit attributes cannot hold <c>decimal</c> constants, so theory data
    /// is written as strings and parsed here, with the invariant culture so "974.999" means the same everywhere.
    /// </summary>
    public static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
