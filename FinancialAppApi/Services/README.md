# Service Lifetime Rule

Singleton services must not take `AppDbContext` or any scoped dependency directly in their constructors.

When singleton or hosted services need database work, create a scope per work item with `IServiceScopeFactory` and resolve scoped services inside that scope. `ReceiptScanBackgroundService` follows this pattern for receipt scan processing.
