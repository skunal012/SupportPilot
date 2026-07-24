namespace SupportPilot.Api.Orders;

// DAY 8-9 — the "live data" the assistant can't get from documents. In a real
// company this would be a call to an Orders microservice or database; here it's
// a small in-memory table so the whole function-calling flow is visible and
// deterministic. SKUs line up with sample-docs/acme-product-catalog.md.

public record OrderItem(string Sku, string Name, int Quantity);

public record Order(
    string OrderId,
    string Status,          // Processing | Shipped | Delivered | Cancelled
    string PlacedDate,
    string? EstimatedDelivery,
    string? Carrier,
    string? TrackingNumber,
    IReadOnlyList<OrderItem> Items,
    string Total);

// A singleton lookup the /orders/{id} endpoint and the get_order tool both read.
public sealed class OrdersStore
{
    private readonly Dictionary<string, Order> _orders = new()
    {
        ["1042"] = new Order(
            OrderId: "1042", Status: "Shipped",
            PlacedDate: "2026-07-20", EstimatedDelivery: "2026-07-26",
            Carrier: "Acme Express", TrackingNumber: "AE123456789US",
            Items: [new OrderItem("AC-SP-200", "SoundPods Pro", 1)],
            Total: "$129.99"),

        ["1043"] = new Order(
            OrderId: "1043", Status: "Processing",
            PlacedDate: "2026-07-23", EstimatedDelivery: null,
            Carrier: null, TrackingNumber: null,
            Items: [new OrderItem("AC-TC-400", "TrailCam 4K", 1)],
            Total: "$199.99"),

        ["1055"] = new Order(
            OrderId: "1055", Status: "Delivered",
            PlacedDate: "2026-07-12", EstimatedDelivery: "2026-07-16",
            Carrier: "Acme Express", TrackingNumber: "AE987654321US",
            Items: [new OrderItem("AC-GD-050", "GlowDesk Lamp", 2)],
            Total: "$99.98"),

        ["1077"] = new Order(
            OrderId: "1077", Status: "Shipped",
            PlacedDate: "2026-07-22", EstimatedDelivery: "2026-07-27",
            Carrier: "Acme Express", TrackingNumber: "AE555222888US",
            Items:
            [
                new OrderItem("AC-PC-020", "PowerCore 20K", 1),
                new OrderItem("AC-KM-060", "KeyBoard Mini", 1),
            ],
            Total: "$99.98"),

        ["2001"] = new Order(
            OrderId: "2001", Status: "Cancelled",
            PlacedDate: "2026-07-10", EstimatedDelivery: null,
            Carrier: null, TrackingNumber: null,
            Items: [new OrderItem("AC-SP-100", "SoundPods Lite", 1)],
            Total: "$69.99"),
    };

    public bool TryGet(string orderId, out Order order) => _orders.TryGetValue(orderId, out order!);
}
