MATCH (n0:`Person`)-[r2:`LIKES`]->(n1:`Movie`)
WHERE NOT EXISTS { MATCH (n0:`Person`)-[r3:`ACTED_IN`]->(n1:`Movie`) }
RETURN DISTINCT n0.`name` AS c0
ORDER BY c0 ASC
LIMIT $p0
