MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)
WHERE n1.`title` = $p0
WITH n0, count(*) AS v3
RETURN n0.`name` AS c0, v3 AS c1
ORDER BY c0 ASC
LIMIT $p1
