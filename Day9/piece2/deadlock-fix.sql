-- ==========================================
-- THE FIX: CONSISTENT LOCK ORDERING
-- ==========================================

-- [SESSION 1] Always update A, then B
BEGIN TRAN;
UPDATE TableA SET Val = 'Session1' WHERE Id = 1;
UPDATE TableB SET Val = 'Session1' WHERE Id = 1;
COMMIT;

-- [SESSION 2] Always update A, then B (Will safely block, not deadlock)
BEGIN TRAN;
UPDATE TableA SET Val = 'Session2' WHERE Id = 1;
UPDATE TableB SET Val = 'Session2' WHERE Id = 1;
COMMIT;
