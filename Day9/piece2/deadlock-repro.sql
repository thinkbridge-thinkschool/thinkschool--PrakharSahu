-- ==========================================
-- SETUP (Run first)
-- ==========================================
DROP TABLE IF EXISTS TableA;
DROP TABLE IF EXISTS TableB;
CREATE TABLE TableA (Id INT PRIMARY KEY, Val VARCHAR(10));
CREATE TABLE TableB (Id INT PRIMARY KEY, Val VARCHAR(10));
INSERT INTO TableA VALUES (1, 'A');
INSERT INTO TableB VALUES (1, 'B');


-- ==========================================
-- REPRODUCE DEADLOCK
-- ==========================================
-- [SESSION 1] Grab lock on A
BEGIN TRAN;
UPDATE TableA SET Val = 'Session1' WHERE Id = 1;

-- [SESSION 2] Grab lock on B
BEGIN TRAN;
UPDATE TableB SET Val = 'Session2' WHERE Id = 1;

-- [SESSION 1] Try to lock B (Hangs)
UPDATE TableB SET Val = 'S1_Wait' WHERE Id = 1;

-- [SESSION 2] Try to lock A (Throws Error 1205 Deadlock Victim)
UPDATE TableA SET Val = 'S2_Wait' WHERE Id = 1;
