### GitHub link
https://github.com/thinkbridge-thinkschool/thinkschool--PrakharSahu/tree/feature/day7-piece1/Day7/piece1

### Exercise
![CTE Result](./cte-result.png)

\\\sql
WITH QuoteRankings AS (
    SELECT 
        u.Email AS AuthorName,
        q.Text AS MostRecentQuote,
        COUNT(q.Id) OVER(PARTITION BY u.Id) AS QuoteCount,
        ROW_NUMBER() OVER(PARTITION BY u.Id ORDER BY q.CreatedAt DESC) AS RecentRank
    FROM Users u
    INNER JOIN Quotes q ON u.Id = q.UserId
)
SELECT 
    AuthorName, 
    QuoteCount, 
    MostRecentQuote
FROM QuoteRankings
WHERE RecentRank = 1
ORDER BY QuoteCount DESC
LIMIT 10;
\\\

**Why a CTE here over a correlated subquery?**
A correlated subquery inside the SELECT forces RBAR (Row-By-Agonizing-Row) execution where the database engine runs separate, nested queries for every single author row. A CTE processes the aggregations and window functions in a single, highly-optimized set-based scan.

### What did you learn this session?
I learned how to write Common Table Expressions combined with window functions (ROW_NUMBER and COUNT OVER PARTITION) to retrieve complex aggregations and top-N-per-category results without triggering N+1 database performance issues.

### What would break this?
If multiple quotes from the same author have the exact same identical CreatedAt timestamp, ROW_NUMBER() becomes non-deterministic and could return a different "most recent" quote on different executions unless a secondary tie-breaker is added to the ORDER BY clause.
