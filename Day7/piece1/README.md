# Day 7, Piece 1: Joins and CTEs

## Exercise
![CTE Result](./cte-result.png)

**SQL Query:**
\\\sql
WITH QuoteRankings AS (
    SELECT 
        u.Name AS AuthorName,
        q.Text AS MostRecentQuote,
        COUNT(q.Id) OVER(PARTITION BY u.Id) AS QuoteCount,
        ROW_NUMBER() OVER(PARTITION BY u.Id ORDER BY q.CreatedAt DESC) AS RecentRank
    FROM Users u
    INNER JOIN Quotes q ON u.Id = q.UserId
)
SELECT AuthorName, QuoteCount, MostRecentQuote
FROM QuoteRankings
WHERE RecentRank = 1
ORDER BY QuoteCount DESC;
\\\

**Why a CTE here over a correlated subquery?**
A correlated subquery in the SELECT clause forces the database engine to execute RBAR (Row-By-Agonizing-Row), meaning it runs a separate aggregate query for every single author returned. A CTE scans the table and computes the aggregations in a single, highly-optimized set-based operation.

## Extra Credit
**What did you learn this session?**
I learned how to combine Common Table Expressions (CTEs) with Window Functions to eliminate N+1 query performance issues directly at the database level.

**What would break this?**
If two quotes from the same author have the exact same CreatedAt timestamp, ROW_NUMBER() might assign '1' to either of them unpredictably. A secondary tie-breaker in the ORDER BY (like q.Id DESC) is needed for true deterministic results.
