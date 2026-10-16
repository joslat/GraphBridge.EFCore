MATCH (n0:`Person`)
OPTIONAL MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)
WITH n0, min(n1.`released`) AS v3, max(n1.`released`) AS v4, collect(DISTINCT n1.`title`)[..$p0] AS v5
RETURN n0.`name` AS c0, v3 AS c1, v4 AS c2, v5 AS c3
ORDER BY c0 ASC
LIMIT $p1
