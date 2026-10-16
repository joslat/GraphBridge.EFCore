MATCH (n0:`Person`)
OPTIONAL MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)
WITH n0, n1
WHERE n1.`released` > $p0
RETURN DISTINCT n0.`name` AS c0, n1.`title` AS c1
ORDER BY c0 ASC
LIMIT $p1
