### GitHub link
https://github.com/thinkbridge-thinkschool/thinkschool--PrakharSahu/tree/feature/day8-piece1/Day8/piece1

### Exercise

**Index DDL:**
\\\sql
CREATE CLUSTERED INDEX CIX_SalesOrders_OrderId ON SalesOrders(OrderId);
CREATE NONCLUSTERED INDEX NCIX_SalesOrders_CustomerId ON SalesOrders(CustomerId);
CREATE NONCLUSTERED INDEX NCIX_SalesOrders_OrderStatus ON SalesOrders(OrderStatus);
\\\

**Queries Used:**
\\\sql
SET STATISTICS IO ON;
SELECT * FROM SalesOrders WHERE OrderId = 55000;
SELECT * FROM SalesOrders WHERE CustomerId = 1234;
SELECT * FROM SalesOrders WHERE OrderStatus = 'Shipped';
\\\

**Logical Reads (Before vs After):**
*   **Before (Heap Table):** Logical reads ~400+ (Full Table Scan).
*   **After Clustered Index (OrderId):** Logical reads = 2 (Clustered Index Seek).
*   **After Non-Clustered (CustomerId):** Logical reads = 3 (Index Seek + Key Lookup).
*   **After Non-Clustered (OrderStatus):** Logical reads = ~300+ (Index Seek, higher cost due to low cardinality).

**Write-side cost observation:**
Adding these indexes significantly increased the computational cost of \INSERT\ and \UPDATE\ statements. The database engine must now synchronously write to the primary clustered structure and simultaneously update two separate B-tree non-clustered indexes on every single transaction.

### What did you learn this session?
I learned how to use \SET STATISTICS IO ON\ to objectively measure query performance. A clustered index dictates the physical sorting of the table (meaning only one can exist), while non-clustered indexes are separate lookup structures.

### What would break this?
Creating too many non-clustered indexes on a table with heavy \INSERT\/\UPDATE\ operations (like a high-traffic logging table) would severely degrade write performance and lead to massive index fragmentation.
