### GitHub link
https://github.com/thinkbridge-thinkschool/thinkschool--PrakharSahu/tree/feature/day8-piece2/Day8/piece2

### Exercise

**Before Plan (Key Lookup):**
The execution plan showed an Index Seek (NonClustered) fetching the CustomerId, joined with a Key Lookup (Clustered) doing an expensive round-trip to the main table just to fetch the OrderDate.

**Index with INCLUDE:**
\\\sql
CREATE NONCLUSTERED INDEX NCIX_SalesOrders_Customer_Cover 
ON SalesOrders(CustomerId) 
INCLUDE (OrderDate);
\\\

**After Plan (Lookup gone):**
The new execution plan showed a single Index Seek (NonClustered). The Key Lookup and Nested Loop were completely eliminated because the index "covered" the entire query.

**Logical-Reads Delta:**
*   **Before:** Higher logical reads because it had to do an index seek plus individual key lookups back to the clustered table for every matching row.
*   **After:** Dropped to just 2 logical reads because the query was served entirely from the covering index.

### What did you learn this session?
I learned what a "Covering Index" is. By using the \INCLUDE\ clause, you can tack non-key columns onto the leaf level of a non-clustered index. This prevents the database engine from having to do an expensive secondary "Key Lookup" to the clustered index to fetch columns that weren't part of the indexed keys.

### What would break this?
If you get greedy and \INCLUDE\ too many columns (or large VARCHAR(MAX) columns) into your indexes, you are essentially just duplicating the entire table on disk. This will drastically inflate your database storage size and severely slow down \INSERT\ and \UPDATE\ performance.
