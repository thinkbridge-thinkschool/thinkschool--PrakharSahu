-- ==========================================
-- SETUP SCRIPT
-- ==========================================
-- CREATE TABLE Accounts (Id INT PRIMARY KEY, Balance DECIMAL(10,2));
-- INSERT INTO Accounts (Id, Balance) VALUES (1, 1000.00), (2, 500.00);

-- ==========================================
-- 1. DIRTY READ
-- ==========================================
-- Session 1
-- BEGIN TRAN;
-- UPDATE Accounts SET Balance = 9999.00 WHERE Id = 1;
-- ROLLBACK;

-- Session 2
-- SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
-- SELECT Balance FROM Accounts WHERE Id = 1; 

-- ==========================================
-- 2. NON-REPEATABLE READ
-- ==========================================
-- Session 1
-- SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
-- BEGIN TRAN;
-- SELECT Balance FROM Accounts WHERE Id = 1; 
-- SELECT Balance FROM Accounts WHERE Id = 1; 
-- COMMIT;

-- Session 2
-- BEGIN TRAN;
-- UPDATE Accounts SET Balance = 8888.00 WHERE Id = 1;
-- COMMIT;

-- ==========================================
-- 3. PHANTOM READ
-- ==========================================
-- Session 1
-- SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
-- BEGIN TRAN;
-- SELECT * FROM Accounts WHERE Balance > 100.00; 
-- SELECT * FROM Accounts WHERE Balance > 100.00; 
-- COMMIT;

-- Session 2
-- BEGIN TRAN;
-- INSERT INTO Accounts (Id, Balance) VALUES (3, 2000.00);
-- COMMIT;
