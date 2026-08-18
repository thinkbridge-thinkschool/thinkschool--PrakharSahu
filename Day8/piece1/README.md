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
*   **Before (Heap Table):** Logical reads were around ~400+ because it was doing a full table scan.
*   **After Clustered Index (OrderId):** Dropped to just 2 reads (Clustered Index Seek).
*   **After Non-Clustered (CustomerId):** Dropped to 3 reads (Index Seek + Key Lookup).
*   **After Non-Clustered (OrderStatus):** Still hovered around 300+ reads because there are only 3 statuses, so the index isn't as helpful here.

**Write-side cost observation:**
I noticed that while the reads got way faster, inserts and updates take a hit. Every time a new row is added, SQL Server doesn't just write to the table—it also has to update both of those non-clustered index trees behind the scenes.

### What did you learn this session?
I finally got to see the actual math on how much of a difference indexes make using \SET STATISTICS IO ON\. Seeing the reads drop from hundreds down to 2 or 3 was pretty eye-opening. It also clicked for me that a table can only have one clustered index since it determines the actual physical order of the data on the disk.

### What would break this?
If we slap way too many non-clustered indexes on a table that gets updated constantly (like a real-time logging table), it would completely tank the write performance because the database has to update every single index on every insert.
