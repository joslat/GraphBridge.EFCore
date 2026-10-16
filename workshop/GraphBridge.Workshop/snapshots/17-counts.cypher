MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)
WITH n0, count(DISTINCT n1) AS v3, count(*) AS v4
RETURN n0.`name` AS c0, v3 AS c1, v4 AS c2
ORDER BY c0 ASC
LIMIT $p0
