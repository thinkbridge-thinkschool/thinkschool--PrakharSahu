-- 1. Nuke the table to guarantee a clean slate
DROP TABLE IF EXISTS SalesOrders;

-- 2. Create the Heap
CREATE TABLE SalesOrders (
    OrderId INT,
    CustomerId INT,
    OrderStatus VARCHAR(20),
    OrderDate DATETIME
);

-- 3. Load the Data
SET NOCOUNT ON;
DECLARE @i INT = 1;
BEGIN TRAN;
WHILE @i <= 100000
BEGIN
    INSERT INTO SalesOrders (OrderId, CustomerId, OrderStatus, OrderDate)
    VALUES (
        @i, 
        ABS(CHECKSUM(NEWID())) % 5000 + 1, 
        CHOOSE(ABS(CHECKSUM(NEWID())) % 3 + 1, 'Shipped', 'Processing', 'Cancelled'), 
        DATEADD(DAY, -(ABS(CHECKSUM(NEWID())) % 365), GETDATE())
    );
    SET @i = @i + 1;
END;
COMMIT TRAN;

-- 4. Turn on IO Statistics
SET STATISTICS IO ON;

PRINT '--- BEFORE INDEXES (HEAP) ---';
SELECT * FROM SalesOrders WHERE OrderId = 55000;
SELECT * FROM SalesOrders WHERE CustomerId = 1234;
SELECT * FROM SalesOrders WHERE OrderStatus = 'Shipped';

PRINT '--- CREATING INDEXES ---';
CREATE CLUSTERED INDEX CIX_SalesOrders_OrderId ON SalesOrders(OrderId);
CREATE NONCLUSTERED INDEX NCIX_SalesOrders_CustomerId ON SalesOrders(CustomerId);
CREATE NONCLUSTERED INDEX NCIX_SalesOrders_OrderStatus ON SalesOrders(OrderStatus);

PRINT '--- AFTER INDEXES ---';
SELECT * FROM SalesOrders WHERE OrderId = 55000;
SELECT * FROM SalesOrders WHERE CustomerId = 1234;
SELECT * FROM SalesOrders WHERE OrderStatus = 'Shipped';

SET STATISTICS IO OFF;
