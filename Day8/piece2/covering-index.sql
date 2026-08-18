SET STATISTICS IO ON;

-- 1. BEFORE
SELECT CustomerId, OrderDate FROM SalesOrders WHERE CustomerId = 1234;

-- 2. CREATE COVERING INDEX
DROP INDEX IF EXISTS NCIX_SalesOrders_CustomerId ON SalesOrders;
CREATE NONCLUSTERED INDEX NCIX_SalesOrders_Customer_Cover 
ON SalesOrders(CustomerId) 
INCLUDE (OrderDate);

-- 3. AFTER
SELECT CustomerId, OrderDate FROM SalesOrders WHERE CustomerId = 1234;

SET STATISTICS IO OFF;
