-- ==========================================
-- SETUP SCRIPT (Run first)
-- ==========================================
DROP TABLE IF EXISTS Accounts;
CREATE TABLE Accounts (Id INT PRIMARY KEY, Balance DECIMAL(10,2));
INSERT INTO Accounts (Id, Balance) VALUES (1, 1000.00), (2, 500.00);


-- ==========================================
-- 1. DIRTY READ
-- ==========================================
-- [SESSION 1]
BEGIN TRAN;
UPDATE Accounts SET Balance = 9999.00 WHERE Id = 1;

-- [SESSION 2]
SELECT Balance FROM Accounts WITH (NOLOCK) WHERE Id = 1; 

-- [SESSION 1] (Cleanup)
ROLLBACK;


-- ==========================================
-- 2. NON-REPEATABLE READ
-- ==========================================
-- [SESSION 1]
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRAN;
SELECT Balance FROM Accounts WHERE Id = 1; 

-- [SESSION 2]
BEGIN TRAN;
UPDATE Accounts SET Balance = 8888.00 WHERE Id = 1;
COMMIT;

-- [SESSION 1] (Notice the balance changed to 8888.00)
SELECT Balance FROM Accounts WHERE Id = 1; 
COMMIT;


-- ==========================================
-- 3. PHANTOM READ
-- ==========================================
-- [SESSION 1]
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRAN;
SELECT * FROM Accounts WHERE Balance > 100.00; 

-- [SESSION 2]
BEGIN TRAN;
INSERT INTO Accounts (Id, Balance) VALUES (3, 2000.00);
COMMIT;

-- [SESSION 1] (Notice a 3rd row magically appeared)
SELECT * FROM Accounts WHERE Balance > 100.00; 
COMMIT;
